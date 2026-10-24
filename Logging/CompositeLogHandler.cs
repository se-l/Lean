/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;

namespace QuantConnect.Logging
{
    /// <summary>
    /// Provides an <see cref="ILogHandler"/> implementation that composes multiple handlers
    /// </summary>
    public class CompositeLogHandler : ILogHandler
    {
        private readonly ILogHandler[] _handlers;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeLogHandler"/> that pipes log messages to the console and log.txt,
        /// plus OTLP when an OpenTelemetry endpoint is configured (see <see cref="OpenTelemetryLogHandler"/>)
        /// </summary>
        public CompositeLogHandler()
            : this(DefaultHandlers())
        {
        }

        /// <summary>
        /// The stock console + file handlers, with OTLP shipping appended only when configured
        /// </summary>
        private static ILogHandler[] DefaultHandlers()
        {
            var handlers = new List<ILogHandler> { new ConsoleLogHandler(), new FileLogHandler() };

            // Opt-in: TryCreate returns null unless OTEL_* env vars point at a collector,
            // so an unconfigured Lean keeps exactly the historical console + log.txt behaviour.
            var otel = OpenTelemetryLogHandler.TryCreate();
            if (otel != null)
            {
                handlers.Add(otel);
            }

            return handlers.ToArray();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeLogHandler"/> class from the specified handlers
        /// </summary>
        /// <param name="handlers">The implementations to compose</param>
        public CompositeLogHandler(params ILogHandler[] handlers)
        {
            if (handlers == null || handlers.Length == 0)
            {
                throw new ArgumentNullException(nameof(handlers));
            }

            _handlers = handlers;
        }

        /// <summary>
        /// Write error message to log
        /// </summary>
        /// <param name="text"></param>
        public void Error(string text)
        {
            foreach (var handler in _handlers)
            {
                handler.Error(text);
            }
        }

        /// <summary>
        /// Write debug message to log
        /// </summary>
        /// <param name="text"></param>
        public void Debug(string text)
        {
            foreach (var handler in _handlers)
            {
                handler.Debug(text);
            }
        }

        /// <summary>
        /// Write debug message to log
        /// </summary>
        /// <param name="text"></param>
        public void Trace(string text)
        {
            foreach (var handler in _handlers)
            {
                handler.Trace(text);
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            foreach (var handler in _handlers)
            {
                handler.Dispose();
            }
        }
    }
}
