using Microsoft.Xna.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod {
    public class MainThreadHelper : GameComponent {

        public static readonly TaskCreationOptions ForceQueue = TaskCreationOptions.LongRunning;

        private class MainThreadTaskScheduler : TaskScheduler {

            public bool TaskIsForceQueued;

            private readonly LinkedList<Task> _TasksList = new();
            private readonly Stopwatch _Stopwatch = new Stopwatch();

            public override int MaximumConcurrencyLevel => 1;

            protected override IEnumerable<Task> GetScheduledTasks() {
                //lock (_TasksList)
                // ReSharper disable once InconsistentlySynchronizedField
                return _TasksList;
            }
            protected override void QueueTask(Task task) {
                lock (_TasksList) 
                    _TasksList.AddLast(task);
            }

            protected override bool TryDequeue(Task task) {
                return false;
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) {
                // We must be on the main thread
                // We don't have to worry about dequeuing as there only is a single thread executing tasks
                if (!IsMainThread)
                    return false;

                // Check that we are not currently enqueuing a force queued task
                if (TaskIsForceQueued)
                    return false;

                if (taskWasPreviouslyQueued) {
                    lock (_TasksList)
                        _TasksList.Remove(task);
                }

                return TryExecuteTask(task);
            }

            public void ExecuteTasksFor(int timeSlice) {
                // Execute tasks during our allocated time slice
                // Always execute a task at least
                if (timeSlice > 0)
                    _Stopwatch.Restart();
                do {
                    Task task;
                    lock (_TasksList) {
                        LinkedListNode<Task> node = _TasksList.First;
                        if (node == null) break;
                        task = node.Value;
                        _TasksList.RemoveFirst();
                    }
                    TryExecuteTask(task);
                } while (_Stopwatch.ElapsedMilliseconds < timeSlice);
                if (timeSlice > 0)
                    _Stopwatch.Stop();
            }

            // Should always be invoked from main thread
            public bool TryRunTaskNow(Task task) {
                Debug.Assert(IsMainThread);
                LinkedListNode<Task> node;
                lock (_TasksList) {
                    node = _TasksList.Find(task);
                    if (node == null) return false;
                }
                
                TryExecuteTask(task);
                
                lock (_TasksList) {
                    _TasksList.Remove(node);
                }
                return true;
            }

        }

        internal static MainThreadHelper Instance;

        public static Thread MainThread { get; private set; }
        public static bool UpdatedOnce { get; private set; }

        public static bool IsMainThread => MainThread == Thread.CurrentThread;

        public static int Boost;
        private static int TaskTimeSlice => Boost != 0 ? Boost : 10;

        private static MainThreadTaskScheduler TaskScheduler;
        private static TaskFactory TaskFactory;

        private static ConcurrentDictionary<object, Task> KeyedTasks = new ConcurrentDictionary<object, Task>();

        static MainThreadHelper() {
            TaskScheduler = new MainThreadTaskScheduler();
            TaskFactory = new TaskFactory(CancellationToken.None, TaskCreationOptions.None, TaskContinuationOptions.None, TaskScheduler);
        }

        public MainThreadHelper(Game game)
            : base(game) {
            Instance = this;

            UpdateOrder = -500000;
            MainThread = Thread.CurrentThread;
        }

        public static ValueTask Schedule(Action act, bool forceQueue = false) {
            if (forceQueue && IsMainThread) {
                try {
                    TaskScheduler.TaskIsForceQueued = true;
                    return new ValueTask(TaskFactory.StartNew(act));
                } finally {
                    TaskScheduler.TaskIsForceQueued = false;
                }
            }

            if (IsMainThread) {
                act();
                return ValueTask.CompletedTask;
            } else
                return new ValueTask(TaskFactory.StartNew(act));
        }

        public static ValueTask<T> Schedule<T>(Func<T> act, bool forceQueue = false) {
            if (forceQueue && IsMainThread) {
                try {
                    TaskScheduler.TaskIsForceQueued = true;
                    return new ValueTask<T>(TaskFactory.StartNew(act));
                } finally {
                    TaskScheduler.TaskIsForceQueued = false;
                }
            }

            if (IsMainThread)
                return new ValueTask<T>(act());
            else
                return new ValueTask<T>(TaskFactory.StartNew(act));
        }
        
        public static ValueTask<T> Schedule<T>(Func<T> act, CancellationToken ct, bool forceQueue = false) {
            if (forceQueue && IsMainThread) {
                try {
                    TaskScheduler.TaskIsForceQueued = true;
                    return new ValueTask<T>(TaskFactory.StartNew(act, ct));
                } finally {
                    TaskScheduler.TaskIsForceQueued = false;
                }
            }

            if (IsMainThread)
                return new ValueTask<T>(act());
            else
                return new ValueTask<T>(TaskFactory.StartNew(act, ct));
        }

        public static Task Schedule(Func<Task> act, bool forceQueue = false) {
            if (forceQueue && IsMainThread) {
                try {
                    TaskScheduler.TaskIsForceQueued = true;
                    return TaskFactory.StartNew(act).Unwrap();
                } finally {
                    TaskScheduler.TaskIsForceQueued = false;
                }
            }

            if (IsMainThread)
                return act();
            else
                return TaskFactory.StartNew(act).Unwrap();
        }

        public static Task<T> Schedule<T>(Func<Task<T>> act, bool forceQueue = false) {
            if (forceQueue && IsMainThread) {
                try {
                    TaskScheduler.TaskIsForceQueued = true;
                    return TaskFactory.StartNew(act).Unwrap();
                } finally {
                    TaskScheduler.TaskIsForceQueued = false;
                }
            }

            if (IsMainThread)
                return act();
            else
                return TaskFactory.StartNew(act).Unwrap();
        }

        // Waits for a task to complete while using the downtime to run MTH tasks
        public static void BusyWaitTask(Task task) {
            if (task.IsCompleted) return;
            if (!IsMainThread) {
                task.Wait();
                return;
            }

            if (TaskScheduler.TryRunTaskNow(task)) {
                return;
            }
            
            while (!task.IsCompleted) {
                TaskScheduler.ExecuteTasksFor(0);
            }
        }

        // Waits for a task to complete while using the downtime to run MTH tasks
        public static T BusyWaitTask<T>(Task<T> task) {
            BusyWaitTask((Task)task);
            if (task.IsCompleted)
                return task.Result;
            else
                throw new InvalidOperationException("Recursive call to BusyWaitTask!");
        }

        public static readonly YieldFrameAwaitable YieldFrame;

        public struct YieldFrameAwaitable {

            public YieldFrameAwaiter GetAwaiter() => default;

            public struct YieldFrameAwaiter : INotifyCompletion {

                public bool IsCompleted => false;
                public void GetResult() {}
                public void OnCompleted(Action continuation) => Schedule(continuation, forceQueue: true);

            }

        }

        [Obsolete("Use Schedule instead")] public static void Do(Action a) => Schedule(a);
        [Obsolete("Use Schedule instead")] public static MaybeAwaitable<T> Get<T>(Func<T> f) => new MaybeAwaitable<T>(Schedule(f));
        [Obsolete("Use Schedule instead")] public static MaybeAwaitable<T> GetForceQueue<T>(Func<T> f) => new MaybeAwaitable<T>(Schedule(f, forceQueue: true));

        [Obsolete("Use Schedule instead")]
        public static void Do(object key, Action act) {
            if (IsMainThread) {
                act();
                return;
            }

            if (KeyedTasks.TryGetValue(key, out Task task) && !task.IsCompleted)
                return;

            lock (KeyedTasks) {
                if (KeyedTasks.TryGetValue(key, out task) && task.IsCompleted)
                    return;

                KeyedTasks[key] = Schedule(act).AsTask();
            }
        }

        [Obsolete("Use Schedule instead")]
        public static MaybeAwaitable<T> Get<T>(object key, Func<T> act) {
            if (IsMainThread)
                return new MaybeAwaitable<T>(act());

            if (!KeyedTasks.TryGetValue(key, out Task task) || task.IsCompleted) {
                lock (KeyedTasks) {
                    if (!KeyedTasks.TryGetValue(key, out task) || task.IsCompleted)
                        KeyedTasks[key] = task = Schedule(act).AsTask();
                }
            }

            return new MaybeAwaitable<T>(((Task<T>) task).GetAwaiter());
        }

        [Obsolete("Use Schedule instead")]
        public static MaybeAwaitable<T> GetForceQueue<T>(object key, Func<T> act) {
            if (IsMainThread)
                return new MaybeAwaitable<T>(act());

            if (!KeyedTasks.TryGetValue(key, out Task task) || task.IsCompleted) {
                lock (KeyedTasks) {
                    if (!KeyedTasks.TryGetValue(key, out task) || task.IsCompleted)
                        KeyedTasks[key] = task = Schedule(act, forceQueue: true).AsTask();
                }
            }

            return new MaybeAwaitable<T>(((Task<T>) task).GetAwaiter());
        }

        public override void Update(GameTime gameTime) {
            UpdatedOnce = true;
            TaskScheduler.ExecuteTasksFor(TaskTimeSlice);

            if (gameTime == null)
                return;

            base.Update(gameTime);
        }

    }

    [Obsolete("This is an obsolete reinvention of a TaskCompletionSource<T>")]
    public struct MaybeAwaitable<T> {

        private MaybeAwaiter _Awaiter;
        public readonly bool IsValid;

        public MaybeAwaitable(T result) => _Awaiter = new MaybeAwaiter(result);
        public MaybeAwaitable(TaskAwaiter<T> task) => _Awaiter = new MaybeAwaiter(task);
        public MaybeAwaitable(Func<bool> canGetResult) => _Awaiter._CanGetResult = canGetResult;
        public MaybeAwaitable(TaskAwaiter<T> task, Func<bool> canGetResult) => _Awaiter = new MaybeAwaiter(task) { _CanGetResult = canGetResult };

        internal MaybeAwaitable(ValueTask<T> task) {
            if (task.IsCompletedSuccessfully)
                _Awaiter = new MaybeAwaiter(task.Result);
            else
                _Awaiter = new MaybeAwaiter(task.AsTask().GetAwaiter());
        }

        public MaybeAwaiter GetAwaiter() => _Awaiter;
        public T GetResult() => _Awaiter.GetResult();
        public void SetResult(T result) => _Awaiter.SetResult(result);

        public struct MaybeAwaiter : ICriticalNotifyCompletion {

            private bool _IsImmediate, _IsWrapper;
            private T _ImmediateVal;
            private TaskAwaiter<T>? _TaskAwaiter;
            private TaskCompletionSource<T> _CompletionSource;
            internal Func<bool> _CanGetResult; // Backwards compat only

            private TaskAwaiter<T> TaskAwaiter => _TaskAwaiter ??= CompletionSource.Task.GetAwaiter();
            private TaskCompletionSource<T> CompletionSource => _CompletionSource ??= new TaskCompletionSource<T>();

            public bool IsCompleted => _IsImmediate || TaskAwaiter.IsCompleted;

            internal MaybeAwaiter(T result) {
                _IsImmediate = true;
                _ImmediateVal = result;
            }

            internal MaybeAwaiter(TaskAwaiter<T> task) {
                _IsWrapper = true;
                _TaskAwaiter = task;
            }

            public T GetResult() {
                if (!_CanGetResult?.Invoke() ?? false)
                    throw new InvalidOperationException("Can't currently get the result of the MaybeAwaiter");
                return _IsImmediate ? _ImmediateVal : TaskAwaiter.GetResult();
            }

            public void SetResult(T result) {
                if (_IsImmediate || _IsWrapper)
                    throw new InvalidOperationException("Can't set the result of a MaybeAwaiter which doesn't expect one");
                CompletionSource.SetResult(result);
            }

            public void OnCompleted(Action continuation) {
                if (_IsImmediate)
                    continuation();
                else
                    TaskAwaiter.OnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation) {
                if (_IsImmediate)
                    continuation();
                else
                    TaskAwaiter.UnsafeOnCompleted(continuation);
            }

        }

    }
}
