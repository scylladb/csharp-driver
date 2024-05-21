﻿//
//       Copyright (C) DataStax Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OpenTelemetry.Trace;

namespace Cassandra.OpenTelemetry.Implementation
{
    internal class Trace : IRequestTracker
    {
        internal static readonly ActivitySource ActivitySource = new ActivitySource(CassandraInstrumentation.ActivitySourceName, CassandraInstrumentation.Version);
        private readonly CassandraInstrumentationOptions _instrumentationOptions;
        private static readonly string otelActivityKey = "otel_activity";
        private static readonly string operationName = "ExecuteAsync";

        public Trace(CassandraInstrumentationOptions instrumentationOptions)
        {
            _instrumentationOptions = instrumentationOptions;
        }

        public Task OnStartAsync(RequestTrackingInfo request)
        {
            var activityName = !string.IsNullOrEmpty(request.Statement.Keyspace) ? $"{operationName} {request.Statement.Keyspace}" : operationName;

            var activity = ActivitySource.StartActivity(activityName, ActivityKind.Client);

            activity?.AddTag("db.system", "cassandra");
            activity?.AddTag("db.operation", operationName);

            if (activity.IsAllDataRequested)
            {
                if (!string.IsNullOrEmpty(request.Statement.Keyspace))
                {
                    activity.AddTag("db.name", request.Statement.Keyspace);
                }

                if (_instrumentationOptions.IncludeDatabaseStatement)
                {
                    activity.AddTag("db.statement", request.Statement.ToString());
                }
            }

            request.Items.TryAdd(otelActivityKey, activity);

            return Task.FromResult(activity as object);
        }

        public Task OnSuccessAsync(RequestTrackingInfo request)
        {
            request.Items.TryGetValue(otelActivityKey, out object context);

            if (context is Activity activity)
            {
                activity?.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task OnErrorAsync(RequestTrackingInfo request, Exception ex)
        {
            request.Items.TryGetValue(otelActivityKey, out object context);

            if (!(context is Activity activity))
            {
                return Task.CompletedTask;
            }

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);

            activity?.Dispose();

            return Task.CompletedTask;
        }

        public Task OnNodeSuccessAsync(RequestTrackingInfo request, HostTrackingInfo hostInfo)
        {
            throw new NotImplementedException();
        }

        public Task OnNodeErrorAsync(RequestTrackingInfo request, HostTrackingInfo hostInfo, Exception ex)
        {
            throw new NotImplementedException();
        }
    }
}
