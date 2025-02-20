using EverestSplash.SDL3;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EverestSplash;

public static class BinaryReaderExtensions {
    /// <summary>
    /// Reads a C style null terminated ASCII string
    /// </summary>
    /// <param name="reader">The binary reader</param>
    /// <returns>A string as read from the stream</returns>
    public static string ReadSZString(this BinaryReader reader)
    {
        var result = new StringBuilder();
        while (true)
        {
            byte b = reader.ReadByte();
            if (0 == b)
                break;
            result.Append((char)b);
        }
        return result.ToString();
    }
    
}

/// <summary>
/// Encapsulates an SDL_Surface,
/// stands for S(DL_)Surface, named like this to prevent ide autocomplete confusion
/// </summary>
public struct SSurface {
    public IntPtr handle;
    public SDL.SDL_Surface surface;

    public static SSurface FromIntPtr(IntPtr ptr) {
        return new SSurface {
            handle = ptr, 
            surface = Marshal.PtrToStructure<SDL.SDL_Surface>(ptr)
        };
    }
}


/// <summary>
/// Encapsulates an SDL_Texture,
/// stands for S(DL_)Texture, named like this to prevent ide autocomplete confusion
/// </summary>
public class STexture : IDisposable {
    public IntPtr Handle { get; private set; }
    public readonly float Width;
    public readonly float Height;
    public readonly long Format;

    /// <summary>
    /// Creates a new texture from a stream, uses FNA3D for image parsing
    /// </summary>
    /// <param name="textureStream">The png as a stream</param>
    /// <param name="renderer">The renderer being used</param>
    /// <returns>A new STexture</returns>
    public static STexture FromStream(Stream textureStream, IntPtr renderer) {
        // Load the image
        IntPtr pixels = EverestSplashWindow.FNA3D.ReadImageStream(textureStream, out int w, out int h, out int _);
        if (pixels == IntPtr.Zero) 
            throw new Exception("Could not read stream!");
        
        // Convert it to a SDL stream
        IntPtr surface = SDL.SDL_CreateSurfaceFrom(w, h, SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888, pixels, w*4);
        // IntPtr surface = SDL.SDL_CreateRGBSurfaceFrom(pixels,
        //     w, 
        //     h, 
        //     8 * 4 /* byte per 4 channels */, 
        //     w * 4, 
        //     0x000000FF, 
        //     0x0000FF00,
        //     0x00FF0000, 
        //     0xFF000000);
        if (surface == IntPtr.Zero) 
            throw new Exception("Could not create surface! " + SDL.SDL_GetError());
        
        // Convert it to a SDL texture on the renderer
        IntPtr texture = SDL.SDL_CreateTextureFromSurface(renderer, surface);
        if (texture == IntPtr.Zero)
            throw new Exception("Could not create texture from surface! " + SDL.SDL_GetError());
        
        // Data has been copied, so this can be freed
        SDL.SDL_DestroySurface(surface);
        
        // Freeing the above surface does not deallocate the pixel data
        EverestSplashWindow.FNA3D.FNA3D_Image_Free(pixels);
        
        // Done!
        return new STexture(texture);
    }

    public STexture(IntPtr handle) {
        Handle = handle;
        if (!SDL.SDL_GetTextureSize(Handle, out Width, out Height))
            throw new Exception("Cannot query texture size: " + SDL.SDL_GetError());
        uint props = SDL.SDL_GetTextureProperties(Handle);
        if (props == 0)
            throw new Exception("Cannot query texture props: " + SDL.SDL_GetError());
        long val = SDL.SDL_GetNumberProperty(props, SDL.SDL_PROP_TEXTURE_FORMAT_NUMBER, 0);
        if (val == 0)
            throw new Exception("Cannot query texture format");
        Format = val;
    }


    public void Dispose() {
        SDL.SDL_DestroyTexture(Handle);
        Handle = IntPtr.Zero;
    }
}