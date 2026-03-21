using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class TaskHandler
    {
        private HashSet<string> _taskNames = new();
        private ConcurrentQueue<TrackedTask> _tasks = new();

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
                trackedTask.Task.RunSynchronously();
                _taskNames.Remove(trackedTask.Name);
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
