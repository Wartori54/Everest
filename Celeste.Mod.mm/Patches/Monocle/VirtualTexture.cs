#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value null

using Celeste.Mod;
using Celeste.Mod.Core;
using Celeste.Mod.Helpers;
using Celeste.Mod.Meta;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Debug = System.Diagnostics.Debug;

#nullable enable

// FTL v2:
// This file hosts the implementation of FTL v2, Fast Texture Loading version 2.
// 
// Its main goal is to offload the work to other threads of reading the texture data, unpacking it 
// and copying it to an array for upload to the GPU, so that the loading process is sped up by
// asynchronously loading textures. Although the GPU uploads are synced back to the main thread.
// This last detail also fixes a bug in vanilla where GPU uploads could crash the game on rare occasions
// on Nvidia gpus.
//
// It is enabled via the static field FtlToggle, so while that is false all loads will happen synchronously.
// It is important to note that only loads invoked from the main thread will be offloaded to other threads,
// this due to the assumption that if loading happens intentionally on a separate thread it is because loads
// are meant to happen on that separate thread only.
//
// You will see that most work is sent to the TextureContentHelper, a helper class whose goal is to do the
// actual loading of the data, as well as capping the current memory usage to not freeze the system (since
// FTL simply offloads all loads onto other threads without checking system pressure).
//
// FTL also implements the ability to lazy load all textures: when a texture is created only its size is read
// and no loading actually occurs, that only when its Texture2D is accessed for the first time. This leads to
// massive gains on the game loading speed but at the cost of constant stutters on gameplay, thus it is not recommended
// unless there are massive memory constraints. Although some mods may use the event
// Everest.Events.VirtualTexture.ShouldForceLazyLoad to force a lazy load for specific textures, in case it has some extra 
// knowledge of when the texture will be used.
// There's also an event to track when a lazily loaded texture is loaded too late (on access) and may cause a gameplay stutter:
// Everest.Events.VirtualTexture.OnLazyLoad.
// It is important to note that not all textures can be preloaded (have its size loaded without fully loading the texture
// itself), for those textures lazy loading will simply not happen.
//
// The FTL v2 implementation also has the added benefit of making VirtualTexture completely thread-safe.
// This does not prevent race conditions on user code though.
//
// For backwards-compatibility sake it is allowed to resize textures (change its Width and Height) properties if and only if 
// those were made using the (string name, int width, int height, Color color) constructor. All resizes will implicitly call
// a reload and consecutively erase the contents if those were somehow modified. An additional property is provided so both
// dimensions can be changed without calling two separate reloads: VirtualTexture.Size.
// For backwards-compatibility sake it is also allowed to override the Texture2D that this VirtualTexture owns, doing so will
// grant ownership of the newly given Texture2D to the VirtualTexture meaning if it were to be `Unload`ed the new Texture2D 
// would be disposed. While the texture is overriden Reloads are nullified. If a reload were to be in progress when the texture
// is overriden, it will be canceled and have no effect. If the texture is overriden while already being overriden the
// VirtualTexture will take ownership of the new one and leave ownership of the old one. Finally, if the texture is overriden
// with a null texture it would have the exact same effect as an Unload call.
// 
// Finally, this class is also tasked with the headless mode loading optimizations, where all textures which can be preloaded
// will have its Texture2D set to a 1x1 texture, this is purely for performance’s sake. Textures which cannot be preloaded will
// be loaded as usual.
namespace Monocle {
    class patch_VirtualTexture : patch_VirtualAsset {

        private string? _path;
        public string? Path {
            [MonoModReplace] get => _path;
            [MonoModReplace]
            private set {
                if (_textureKind != TextureKind.FileSystem || _path != null) 
                    throw new InvalidOperationException("Cannot assign to path!");
                _path = value;
            }
        }
        
        private Color color;
        private int _orig_width;
        private int _orig_height;

        // Makes sure _orig_width and _orig_height are updateable on SizeDefined textures
        protected override void HandleSizeChange() {
            if (_textureKind != TextureKind.SizeDefined)
                throw new InvalidOperationException("Resizing a VirtualTexture is only allowed for size defined textures!");
            lock (_textureLock) {
                _orig_width = _width;
                _orig_height = _height;
            }
        }

        // Helper property to modify both with and height without calling reload twice
        public Point Size {
            get => new(_width, _height);
            set {
                lock (_textureLock) {
                    _width = value.X;
                    _height = value.Y;
                    HandleSizeChange();
                }
            }
        }

        // Texture is mapped to Texture_Safe, and we use _textureTask as the underlying field, so this is not needed
        [MonoModRemove] 
        public Texture2D? Texture;

        /// <summary>
        /// Returns the current texture, and forces a reload if necessary.
        /// </summary>
        /// <exception cref="AggregateException">Thrown if the reload happened asynchronously and there was an exception during it.</exception>
        [MonoModLinkFrom("Microsoft.Xna.Framework.Graphics.Texture2D Monocle.VirtualTexture::Texture")]
        public Texture2D? Texture_Safe {
            get {
                lock (_textureLock) {
                    // The lazy part is never used, unless the texture was lazy loaded and had not started loading until now
                    if (!_textureTask.IsValueCreated) {
                        Logger.Debug(nameof(VirtualTexture), $"Loading texture {Name ?? "(Unnamed)"} on texture access!");
                    }
                    
                    if (!MainThreadHelper.IsMainThread) {
                        return _textureTask.Value.Result;
                    } else {
                        // TODO: Flush from MainThreadHelper if on main thread
                        while (!_textureTask.Value.IsCompleted) {
                            MainThreadHelper.Instance.Update(null);
                        }
                        return _textureTask.Value.Result;
                    }
                }
            }
            set {
                // It does not make much sense to assign to the texture, but some mods do, and vanilla allows for that to happen.
                // Un-synchronized assignments will often lead to race conditions, but there's not much we can do other than keep the state of this object valid.
                // Note that this property will never return null, thus we define assigning null to it as just unloading it.
                if (value == null) {
                    Unload();
                    return;
                }
                lock (_textureLock) {
                    CancelLoad();
                    _textureTask = new Lazy<Task<Texture2D>>(Task.FromResult(value));
                    _width = value.Width;
                    _height = value.Height;
                }
            }
        }
        
        /// <summary>
        /// This is not thread safe.
        /// </summary>
        public bool IsLoaded => _textureTask is { IsValueCreated: true, Value.IsCompletedSuccessfully: true };

        public readonly ModAsset? Metadata;

        private readonly TextureKind _textureKind;
        private readonly object _textureLock;
        private CancellationTokenSource _cts;
        private TextureLoader.IPreLoader _preLoader;
        private Lazy<Task<Texture2D>> _textureTask; // This is the new Texture_Unsafe

        // The main FTL toggle, see this class header for all the details
        public static bool FtlToggle { get; internal set; }

        [MonoModConstructor]
        [MonoModReplace]
        internal patch_VirtualTexture(string path) {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _textureKind = TextureKind.FileSystem;
            Path = path;
            Name = path;
            _textureLock = new object();
            _preLoader = CreatePreLoader();
            _cts = new CancellationTokenSource();
            _textureTask = null!;
            InitializeTexture();
        }

        [MonoModConstructor]
        [MonoModReplace]
        internal patch_VirtualTexture(string name, int width, int height, Color color) {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
            _textureKind = TextureKind.SizeDefined;
            Name = name;
            _width = width;
            _height = height;
            this.color = color;
            _textureLock = new object();
            _preLoader = CreatePreLoader();
            _cts = new CancellationTokenSource();
            _textureTask = null!;
            InitializeTexture();
        }

        [MonoModConstructor]
        internal patch_VirtualTexture(ModAsset metadata) {
            ArgumentNullException.ThrowIfNull(metadata);
            _textureKind = TextureKind.ModAsset;
            Metadata = metadata;
            Name = metadata.PathVirtual;
            _textureLock = new object();
            _preLoader = CreatePreLoader();
            _cts = new CancellationTokenSource();
            _textureTask = null!;
            InitializeTexture();
        }
        
        /// <summary>
        /// Causes a reload (or just load) of the texture, it may complete asynchronously.
        /// </summary>
        [MonoModReplace]
        internal override sealed void Reload() {
            lock (_textureLock) {
                CancelLoad(); // Canceling is required because it disposes the texture if it got loaded
                // We need to reload the preloader too, in case there were any changes affecting it
                _preLoader = CreatePreLoader();
                InitializeTexture(false);
            }
        }
        
        /// <summary>
        /// Unloads the texture from video memory.
        /// </summary>
        [MonoModReplace]
        internal override void Unload() {
            lock (_textureLock) {
                CancelLoad(); // Canceling is required because it disposes the texture if it got loaded
                InitializeTexture(true);
            }
        }

        // IL patch is possible here, is it worth it though? (IL should not change that much)
        /// <summary>
        /// Disposes the native resources and unregisters itself.
        /// </summary>
        [MonoModReplace]
        public override void Dispose() {
            Unload();
            // Texture_Unsafe = null;
            patch_VirtualContent.Remove(this);
        }
        
        private TextureLoader.IPreLoader CreatePreLoader() {
            switch (_textureKind) {
                case TextureKind.FileSystem: {
                    Debug.Assert(Path is not null);
                    return System.IO.Path.GetExtension(Path) switch {
                        ".data" => new DataTextureLoader.DataPreLoader(StreamProvider),
                        ".png" => new PNGTextureLoader.PNGPreLoader(StreamProvider, Path),
                        ".xnb" => new XnbTextureLoader.XnbPreLoader(Path),
                        _ => new FallbackTextureLoader.FallbackPreLoader(StreamProvider, false)
                    };
                    break;
                    Stream StreamProvider() => File.OpenRead(System.IO.Path.Combine(Engine.ContentDirectory, Path));
                }
                case TextureKind.ModAsset: {
                    Debug.Assert(Metadata is not null);
                    // Old FTL code used to check if StreamProvider() == null, and if so assigned a fallback
                    // But this would have crashed on the old Preload function before this could happen so the
                    // new impl omits this check and assumes that it doesn't ever happen.
                    Debug.Assert(StreamProvider() is not null);
                    bool premul = false; // Assume unpremultiplied by default
                    if (Metadata.TryGetMeta(out TextureMeta meta))
                        premul = meta.Premultiplied;
                    if (Metadata.Format == "png") {
                        return new PNGTextureLoader.PNGPreLoader(StreamProvider, Name, !Metadata.StreamAsync);
                    } else {
                        return new FallbackTextureLoader.FallbackPreLoader(StreamProvider, premul);
                    }
                    break;
                    Stream StreamProvider() => Metadata!.Stream;
                }
                case TextureKind.SizeDefined: {
                    Debug.Assert(_width > 0 && _height > 0);
                    return new SizeDefinedTextureLoader.SizeDefinedPreLoader(_width, _height, color);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
        }
        
        // Extra setup common in all constructors
        [MemberNotNull(nameof(_textureTask))]
        private void InitializeTexture(bool? shouldLazy = null) {
            if (Everest.Flags.IsHeadless) {
                // On headless we always lazyload for performance reasons, so this has no use
                Everest.Events.VirtualTexture.OnShouldForceLazyLoad((VirtualTexture) (object) this);
                // If a preload is not possible just load the texture, even on headless
                // otherwise we risk having the wrong size, skipping loads entirely is just a
                // performance optimization
                if (!_preLoader.CouldPreload && (_orig_width <= 0 || _orig_height <= 0)) {
                    _textureTask = new Lazy<Task<Texture2D>>(CreateTask(_cts.Token));
                } else {
                    _textureTask = new Lazy<Task<Texture2D>>(Task.FromResult(new Texture2D(Engine.Graphics.GraphicsDevice, 1, 1)));
                }
            } else {
                // Try to lazily create the task eagerly, if there's no preload EnsurePublicFields will load it anyway
                // TODO: should the event be called if shouldLazy == true??
                bool doLazyLoad = Everest.Events.VirtualTexture.OnShouldForceLazyLoad((VirtualTexture) (object) this) || CoreModule.Settings.LazyLoading;
                if (shouldLazy.HasValue) {
                    doLazyLoad = shouldLazy.Value;
                }
                if (doLazyLoad) {
                    _textureTask = new Lazy<Task<Texture2D>>(() => CreateTask(_cts.Token));
                } else {
                    _textureTask = new Lazy<Task<Texture2D>>(CreateTask(_cts.Token));
                }
            }
            EnsurePublicFields();
        }

        // Creates the load task in the appropriate task scheduler
        private Task<Texture2D> CreateTask(CancellationToken ct) {
            // Note: CouldPreload == true is also equivalent to being able to run asynchronously
            if (FtlToggle && _preLoader.CouldPreload) {
                return Task.Factory.StartNew(AsyncLoad, ct).Unwrap();
            }
            return MainThreadHelper.Schedule(BlockingLoad, ct).AsTask();
            
            // TODO: This can likely be deduplicated
            Texture2D BlockingLoad() {
                using TextureLoader loader = _preLoader.CreateLoader();
                loader.AsyncLoad(ct);
                Texture2D loadedTex = loader.SyncLoad();
                return loadedTex;
            }

            async Task<Texture2D> AsyncLoad() {
                using TextureLoader loader = _preLoader.CreateLoader();
                loader.AsyncLoad(ct);
                ct.ThrowIfCancellationRequested();
                Texture2D loadedTex = await MainThreadHelper.Schedule(loader.SyncLoad, ct);
                return loadedTex;
            }
        }

        // Makes sure that the non lazily loaded fields get initialized, blocking if needed
        private void EnsurePublicFields() {
            // Blocking is only needed on first load, this also allows for an "Unloaded" state
            if (_orig_width > 0 && _orig_height > 0) {
                _width = _orig_width;
                _height = _orig_height;
                return;
            }
            if (_preLoader.CouldPreload) {
                Point size = _preLoader.GetPreloadedSize();
                _width = size.X;
                _height = size.Y;
            } else {
                // This cannot block main thread due to MainThreadHelper.Schedule running the task
                // automatically if the calling thread is main thread
                Texture2D tex = _textureTask.Value.Result;
                _width = tex.Width;
                _height = tex.Height;
            }
            _orig_width = _width;
            _orig_height = _height;
        }
        
        // Should always be in a lock
        private void CancelLoad() {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            if (IsLoaded) {
                _textureTask.Value.Result.Dispose();
            }
        }

        private enum TextureKind {
            FileSystem,
            ModAsset,
            SizeDefined
        }
    }

#nullable disable
    public static class VirtualTextureExt {

        /// <summary>
        /// If the VirtualTexture originates from a mod, get the mod asset metadata.
        /// </summary>
        [Obsolete("Use VirtualTexture.Metadata instead.")]
        public static ModAsset GetMetadata(this VirtualTexture self)
            => ((patch_VirtualTexture) (object) self).Metadata;

        /// <summary>
        /// Set a fallback texture in case the texture becomes unavailable on reload.
        /// </summary>
        [Obsolete("Use VirtualTexture.Fallback instead.")]
        public static void SetFallback(this VirtualTexture self, VirtualTexture fallback) {
            //=> ((patch_VirtualTexture) (object) self).Fallback = fallback;
        }

    }
}
