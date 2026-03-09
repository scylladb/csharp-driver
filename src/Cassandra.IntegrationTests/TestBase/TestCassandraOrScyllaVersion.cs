using System;
using Cassandra.IntegrationTests.TestClusterManagement;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Cassandra.IntegrationTests.TestBase
{
    public class TestCassandraOrScyllaVersion : NUnitAttribute, IApplyToTest
    {
        public int CassandraMajor { get; }

        public int CassandraMinor { get; }

        public int CassandraBuild { get; }

        public int ScyllaMajor { get; }

        public int ScyllaMinor { get; }

        public int ScyllaBuild { get; }

        public Comparison Comparison { get; }

        public TestCassandraOrScyllaVersion(
            int cassandraMajor,
            int cassandraMinor,
            int scyllaMajor,
            int scyllaMinor,
            Comparison comparison = Comparison.GreaterThanOrEqualsTo)
            : this(cassandraMajor, cassandraMinor, 0, scyllaMajor, scyllaMinor, 0, comparison)
        {
        }

        public TestCassandraOrScyllaVersion(
            int cassandraMajor,
            int cassandraMinor,
            int cassandraBuild,
            int scyllaMajor,
            int scyllaMinor,
            int scyllaBuild,
            Comparison comparison = Comparison.GreaterThanOrEqualsTo)
        {
            CassandraMajor = cassandraMajor;
            CassandraMinor = cassandraMinor;
            CassandraBuild = cassandraBuild;
            ScyllaMajor = scyllaMajor;
            ScyllaMinor = scyllaMinor;
            ScyllaBuild = scyllaBuild;
            Comparison = comparison;
        }

        public void ApplyToTest(NUnit.Framework.Internal.Test test)
        {
            if (!Applies(out string msg))
            {
                test.RunState = RunState.Ignored;
                test.Properties.Set("_SKIPREASON", msg);
            }
        }

        public bool Applies(out string msg)
        {
            if (TestClusterManager.IsScylla)
            {
                var expectedScyllaVersion = new Version(ScyllaMajor, ScyllaMinor, ScyllaBuild);
                return TestScyllaVersion.VersionMatch(expectedScyllaVersion, Comparison, out msg);
            }

            var expectedCassandraVersion = new Version(CassandraMajor, CassandraMinor, CassandraBuild);
            return TestCassandraVersion.VersionMatch(expectedCassandraVersion, Comparison, out msg);
        }
    }
}
