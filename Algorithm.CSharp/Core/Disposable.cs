using System;
using System.IO;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class Disposable : IDisposable
    {
        protected Foundations _algo { get; set; }
        protected StreamWriter _writer { get; set;}

        public virtual void Dispose()
        {
            if (_writer == null)
            {
                _algo.Log("sadfds");
                _algo.Log($"{this.GetType().BaseType.Name}.Write(): _writer is null.");
                return;
            }
            else if (_writer.BaseStream == null)
            {
                _algo.Log($"{this.GetType().BaseType.Name}.Write(): _writer is closed.");
                return;
            }

            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
