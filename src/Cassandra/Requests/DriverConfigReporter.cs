//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cassandra.Requests
{
    /// <inheritdoc />
    internal class DriverConfigReporter : IDriverConfigReporter
    {
        /// <summary>
        /// <c>STARTUP</c> option holding the JSON description of the effective driver configuration.
        /// </summary>
        internal const string DriverConfigOption = "DRIVER_CONFIG";

        /// <summary>
        /// Major version of the reported configuration schema. Adding keys to the report is backwards
        /// compatible and does not bump it, only changing or removing the meaning of an existing key does.
        /// </summary>
        internal const int SchemaVersion = 1;

        /// <summary>
        /// Upper bound for the length, in bytes, of the <c>DRIVER_CONFIG</c> value.
        /// <para>
        /// <see cref="FrameWriter.WriteString"/> prefixes every <c>STARTUP</c> value with an unchecked 16 bit
        /// length, so a longer value would silently truncate that prefix modulo 65536 while still writing the
        /// whole body, corrupting the frame and failing the handshake. The report is a handful of bytes for
        /// now, but the configuration groups added later describe user supplied values, such as the settings
        /// of custom policies, and can grow arbitrarily large. Enforcing a limit here keeps a connection from
        /// ever being broken by what is only a diagnostic aid.
        /// </para>
        /// <para>
        /// 32 KiB rather than the protocol's own 65535 byte ceiling for this prefix: real world reports are
        /// expected to be well under a couple kilobytes, so this leaves ample headroom while still being far
        /// short of the point where the value would stop protecting anything.
        /// </para>
        /// </summary>
        internal const int MaxDriverConfigLength = 32 * 1024;

        private static readonly Logger Logger = new Logger(typeof(DriverConfigReporter));

        public void AddStartupOptions(IDictionary<string, string> startupOptions)
        {
            string report;
            try
            {
                report = BuildReport();
            }
            catch (Exception ex)
            {
                DriverConfigReporter.Logger.Warning(
                    "Could not build the driver configuration report, it will not be reported to the cluster: {0}", ex);
                return;
            }

            var length = Encoding.UTF8.GetByteCount(report);
            if (length > DriverConfigReporter.MaxDriverConfigLength)
            {
                DriverConfigReporter.Logger.Warning(
                    "The driver configuration report is {0} bytes long, which exceeds the {1} bytes limit, " +
                    "it will not be reported to the cluster.", length, DriverConfigReporter.MaxDriverConfigLength);
                return;
            }

            startupOptions[DriverConfigReporter.DriverConfigOption] = report;
        }

        /// <summary>
        /// Builds the JSON configuration report. It is built for every control connection rather than cached,
        /// so that it always describes the configuration as it is at that point in time.
        /// </summary>
        /// <remarks>
        /// <c>protected virtual</c> so tests can override it (via <c>InternalsVisibleTo</c>) to exercise the
        /// oversize and exception guards in <see cref="AddStartupOptions"/>, which the fixed schema-only
        /// report produced here cannot trigger on its own.
        /// </remarks>
        protected virtual string BuildReport()
        {
            var report = new JObject { ["version"] = DriverConfigReporter.SchemaVersion };
            PopulateConfig(report);
            return report.ToString(Formatting.None);
        }

        /// <summary>
        /// Extension point for subclasses to add further configuration groups to the report. Empty for now.
        /// </summary>
        protected virtual void PopulateConfig(JObject report)
        {
        }
    }
}
