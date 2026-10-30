using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>
    /// Work queued from event handlers and executed on the algorithm thread at the end of OnData.
    /// RunSynchronously() means every queued task BLOCKS the algorithm thread in turn: on an option
    /// fill that is RequestPfRiskScenarios + RequestStressTestDs + OnOptionFill back to back, each
    /// doing blocking pricer round trips. That is the prime suspect for multi-second OnData stalls,
    /// so each task is timed and reported by name.
    /// </summary>
    public class TaskHandler
    {
        private readonly HashSet<string> _taskNames = new();
        private readonly ConcurrentQueue<TrackedTask> _tasks = new();

        /// <summary>Called after each task with (name, elapsed ms), on the algorithm thread.</summary>
        public Action<string, double> Timed { get; set; }

        public void Add(string name, Task task)
        {
            if (_taskNames.Add(name))
            {
                // If the name was not already present, we can safely add the task
                // to the queue and track it.
                _tasks.Enqueue(new TrackedTask(name, task));
            }
        }

        public void RunTasks()
        {
            while (_tasks.TryDequeue(out TrackedTask trackedTask))
            {
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    trackedTask.Task.RunSynchronously();
                }
                finally
                {
                    _taskNames.Remove(trackedTask.Name);
                    Timed?.Invoke(trackedTask.Name, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                }
            }
        }
    }

    public class TrackedTask
    {
        public string Name { get; }
        public Task Task { get; }

        public TrackedTask(string name, Task task)
        {
            Name = name;
            Task = task;
        }
    }
}
