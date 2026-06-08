using System;
using System.Collections.Generic;
using System.Linq;
using Cassandra.Requests;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
#pragma warning disable 618

namespace Cassandra.Tests
{
    [TestFixture]
    public class SerialConsistencyLwtTests
    {
        [Test]
        public void SimpleStatement_With_Serial_Consistency_Should_Be_Lwt()
        {
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            Assert.False(statement.IsLwt(), "SimpleStatement is never a confirmed LWT");
            Assert.True(statement.ShouldRouteAsLwt(), "SimpleStatement with Serial consistency should route as LWT");
        }

        [Test]
        public void SimpleStatement_With_LocalSerial_Consistency_Should_Be_Lwt()
        {
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.LocalSerial);
            Assert.False(statement.IsLwt(), "SimpleStatement is never a confirmed LWT");
            Assert.True(statement.ShouldRouteAsLwt(), "SimpleStatement with LocalSerial consistency should route as LWT");
        }

        [Test]
        public void SimpleStatement_Without_Serial_Consistency_Should_Not_Be_Lwt()
        {
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Quorum);
            Assert.False(statement.IsLwt(), "SimpleStatement with Quorum consistency should not be detected as LWT");
            Assert.False(statement.ShouldRouteAsLwt(), "SimpleStatement with Quorum consistency should not route as LWT");
        }

        [Test]
        public void SimpleStatement_Without_Consistency_Should_Not_Be_Lwt()
        {
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            Assert.False(statement.IsLwt(), "SimpleStatement without explicit consistency should not be detected as LWT");
            Assert.False(statement.ShouldRouteAsLwt(), "SimpleStatement without explicit consistency should not route as LWT");
        }

        [Test]
        public void BoundStatement_With_Serial_Consistency_Should_Be_Lwt()
        {
            var ps = new PreparedStatement();
            ps.SetLwt(false);
            var bound = new BoundStatement(ps);
            bound.SetConsistencyLevel(ConsistencyLevel.Serial);
            Assert.False(bound.IsLwt(), "BoundStatement without prepared LWT should not be confirmed LWT");
            Assert.True(bound.ShouldRouteAsLwt(), "BoundStatement with Serial consistency should route as LWT");
        }

        [Test]
        public void BoundStatement_With_LocalSerial_Consistency_Should_Be_Lwt()
        {
            var ps = new PreparedStatement();
            ps.SetLwt(false);
            var bound = new BoundStatement(ps);
            bound.SetConsistencyLevel(ConsistencyLevel.LocalSerial);
            Assert.False(bound.IsLwt(), "BoundStatement without prepared LWT should not be confirmed LWT");
            Assert.True(bound.ShouldRouteAsLwt(), "BoundStatement with LocalSerial consistency should route as LWT");
        }

        [Test]
        public void BoundStatement_With_PreparedLwt_Should_Be_Lwt()
        {
            var ps = new PreparedStatement();
            ps.SetLwt(true);
            var bound = new BoundStatement(ps);
            bound.SetConsistencyLevel(ConsistencyLevel.Quorum);
            Assert.True(bound.IsLwt(), "BoundStatement from LWT prepared statement should be confirmed LWT");
            Assert.True(bound.ShouldRouteAsLwt(), "BoundStatement from LWT prepared statement should route as LWT");
        }

        [Test]
        public void BoundStatement_Without_Serial_Or_PreparedLwt_Should_Not_Be_Lwt()
        {
            var ps = new PreparedStatement();
            ps.SetLwt(false);
            var bound = new BoundStatement(ps);
            bound.SetConsistencyLevel(ConsistencyLevel.One);
            Assert.False(bound.IsLwt(), "BoundStatement without serial CL or prepared LWT should not be detected as LWT");
            Assert.False(bound.ShouldRouteAsLwt(), "BoundStatement without serial CL or prepared LWT should not route as LWT");
        }

        [Test]
        public void RetryPolicy_Should_Not_Downgrade_Serial_Consistency_On_ReadTimeout()
        {
            var config = new Configuration();
            var policy = DowngradingConsistencyRetryPolicy.Instance.Wrap(Cassandra.Policies.DefaultExtendedRetryPolicy);
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            statement.SetRetryPolicy(policy);

            // Simulate a read timeout with Serial consistency where 1 of 2 responded (received < required triggers downgrade)
            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new ReadTimeoutException(ConsistencyLevel.Serial, 1, 2, false),
                policy, statement, config, 0);

            // The downgrading policy would normally retry at CL.One, but serial should not be downgraded
            Assert.AreEqual(RetryDecision.RetryDecisionType.Rethrow, decision.DecisionType,
                "Retry policy should not downgrade Serial consistency");
        }

        [Test]
        public void RetryPolicy_Should_Not_Downgrade_LocalSerial_Consistency_On_ReadTimeout()
        {
            var config = new Configuration();
            var policy = DowngradingConsistencyRetryPolicy.Instance.Wrap(Cassandra.Policies.DefaultExtendedRetryPolicy);
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.LocalSerial);
            statement.SetRetryPolicy(policy);

            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new ReadTimeoutException(ConsistencyLevel.LocalSerial, 1, 2, false),
                policy, statement, config, 0);

            Assert.AreEqual(RetryDecision.RetryDecisionType.Rethrow, decision.DecisionType,
                "Retry policy should not downgrade LocalSerial consistency");
        }

        [Test]
        public void RetryPolicy_Should_Not_Downgrade_Serial_Consistency_On_Unavailable()
        {
            var config = new Configuration();
            var policy = DowngradingConsistencyRetryPolicy.Instance.Wrap(Cassandra.Policies.DefaultExtendedRetryPolicy);
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            statement.SetRetryPolicy(policy);

            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new UnavailableException(ConsistencyLevel.Serial, 2, 1),
                policy, statement, config, 0);

            Assert.AreEqual(RetryDecision.RetryDecisionType.Rethrow, decision.DecisionType,
                "Retry policy should not downgrade Serial consistency on unavailable");
        }

        [Test]
        public void RetryPolicy_Should_Allow_Retry_With_Same_Serial_Consistency()
        {
            var config = new Configuration();
            // DefaultRetryPolicy retries at same CL on read timeout when data not retrieved
            var policy = new DefaultRetryPolicy();
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            statement.SetRetryPolicy(policy);

            // receivedResponses >= requiredResponses and !dataRetrieved => retry at same CL
            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new ReadTimeoutException(ConsistencyLevel.Serial, 2, 2, false),
                policy, statement, config, 0);

            // Retrying at Serial is OK (no downgrade)
            Assert.AreEqual(RetryDecision.RetryDecisionType.Retry, decision.DecisionType,
                "Retry at same serial consistency should be allowed");
            Assert.AreEqual(ConsistencyLevel.Serial, decision.RetryConsistencyLevel,
                "Retry should keep Serial consistency level");
            Assert.IsTrue(decision.UseCurrentHost,
                "LWT retry should use current host");
        }

        [Test]
        public void RetryPolicy_Should_Rethrow_On_Unavailable_With_Serial_Consistency_Even_If_Policy_Retries_At_Same_Cl()
        {
            var config = new Configuration();
            // Create a policy that retries at the same serial CL on unavailable
            var mockPolicy = new Mock<IExtendedRetryPolicy>();
            mockPolicy
                .Setup(p => p.OnUnavailable(It.IsAny<IStatement>(), It.IsAny<ConsistencyLevel>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(RetryDecision.Retry(ConsistencyLevel.Serial, true));
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            statement.SetRetryPolicy(mockPolicy.Object);

            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new UnavailableException(ConsistencyLevel.Serial, 2, 1),
                mockPolicy.Object, statement, config, 0);

            // Even if the policy says retry at same serial CL, UnavailableException means node is down - must rethrow
            Assert.AreEqual(RetryDecision.RetryDecisionType.Rethrow, decision.DecisionType,
                "UnavailableException with serial consistency should always rethrow for LWT safety");
        }

        [Test]
        public void RetryPolicy_Should_Force_Current_Host_On_ReadTimeout_With_Serial_Consistency()
        {
            var config = new Configuration();
            // Create a policy that retries on a different host
            var mockPolicy = new Mock<IExtendedRetryPolicy>();
            mockPolicy
                .Setup(p => p.OnReadTimeout(It.IsAny<IStatement>(), It.IsAny<ConsistencyLevel>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>()))
                .Returns(RetryDecision.Retry(ConsistencyLevel.Serial, false));
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            statement.SetRetryPolicy(mockPolicy.Object);

            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new ReadTimeoutException(ConsistencyLevel.Serial, 2, 2, false),
                mockPolicy.Object, statement, config, 0);

            // For LWT read timeout (node is up), retry must stay on current host
            Assert.AreEqual(RetryDecision.RetryDecisionType.Retry, decision.DecisionType,
                "ReadTimeout with serial consistency should allow retry");
            Assert.AreEqual(ConsistencyLevel.Serial, decision.RetryConsistencyLevel,
                "Retry should keep Serial consistency level");
            Assert.IsTrue(decision.UseCurrentHost,
                "LWT retry on ReadTimeout should be forced to use current host");
        }

        [Test]
        public void RetryPolicy_Should_Force_Current_Host_On_WriteTimeout_With_Serial_Consistency()
        {
            var config = new Configuration();
            // Create a policy that retries on a different host
            var mockPolicy = new Mock<IExtendedRetryPolicy>();
            mockPolicy
                .Setup(p => p.OnWriteTimeout(It.IsAny<IStatement>(), It.IsAny<ConsistencyLevel>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(RetryDecision.Retry(ConsistencyLevel.Serial, false));
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Serial);
            statement.SetRetryPolicy(mockPolicy.Object);

            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new WriteTimeoutException(ConsistencyLevel.Serial, 2, 2, "CAS"),
                mockPolicy.Object, statement, config, 0);

            // For LWT write timeout (node is up), retry must stay on current host
            Assert.AreEqual(RetryDecision.RetryDecisionType.Retry, decision.DecisionType,
                "WriteTimeout with serial consistency should allow retry");
            Assert.AreEqual(ConsistencyLevel.Serial, decision.RetryConsistencyLevel,
                "Retry should keep Serial consistency level");
            Assert.IsTrue(decision.UseCurrentHost,
                "LWT retry on WriteTimeout should be forced to use current host");
        }

        [Test]
        public void RetryPolicy_Should_Allow_Downgrade_For_NonSerial_Consistency()
        {
            var config = new Configuration();
            var policy = DowngradingConsistencyRetryPolicy.Instance.Wrap(Cassandra.Policies.DefaultExtendedRetryPolicy);
            var statement = new SimpleStatement("SELECT v FROM ks.t WHERE pk = ?");
            statement.SetConsistencyLevel(ConsistencyLevel.Quorum);
            statement.SetRetryPolicy(policy);

            // received < required triggers downgrade to CL.One — guard should not interfere for non-serial
            var decision = RequestHandlerTests.GetRetryDecisionFromServerError(
                new ReadTimeoutException(ConsistencyLevel.Quorum, 1, 3, false),
                policy, statement, config, 0);

            // DowngradingConsistencyRetryPolicy downgrades to CL.One - that's fine for non-serial
            Assert.AreEqual(RetryDecision.RetryDecisionType.Retry, decision.DecisionType,
                "Downgrade for non-serial consistency should still be allowed");
        }
    }
}
