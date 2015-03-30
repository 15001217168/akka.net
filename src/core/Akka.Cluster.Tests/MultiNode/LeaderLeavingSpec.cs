﻿using System;
using System.Linq;
using Akka.Actor;
using Akka.Remote.TestKit;
using Akka.TestKit;

namespace Akka.Cluster.Tests.MultiNode
{
    public class LeaderLeavingSpecConfig : MultiNodeConfig
    {
        private readonly RoleName _first;

        public RoleName First
        {
            get { return _first; }
        }

        private readonly RoleName _second;

        public RoleName Second
        {
            get { return _second; }
        }

        private readonly RoleName _third;

        public RoleName Third
        {
            get { return _third; }
        }

        public LeaderLeavingSpecConfig()
        {
            _first = Role("first");
            _second = Role("second");
            _third = Role("third");

            CommonConfig = MultiNodeLoggingConfig.LoggingConfig
                .WithFallback(DebugConfig(true))
                .WithFallback(@"akka.cluster.auto-down-unreachable-after = 0s
akka.cluster.publish-stats-interval = 25 s")
                .WithFallback(MultiNodeClusterSpec.ClusterConfigWithFailureDetectorPuppet());
        }

        public class ALeaderLeavingMultiNode1 : LeaderLeavingSpec
        {
        }

        public class ALeaderLeavingMultiNode2 : LeaderLeavingSpec
        {
        }

        public class ALeaderLeavingMultiNode3 : LeaderLeavingSpec
        {
        }

        public abstract class LeaderLeavingSpec : MultiNodeClusterSpec
        {
            private readonly LeaderLeavingSpecConfig _config;

            protected LeaderLeavingSpec()
                : this(new LeaderLeavingSpecConfig())
            {
            }

            private LeaderLeavingSpec(LeaderLeavingSpecConfig config) : base(config)
            {
                _config = config;
            }

            [MultiNodeFact]
            public void
                ALeaderThatIsLeavingMustBeMovedToLeavingThenExitingThenRemovedThenBeShutDownAndThenANewLeaderShouldBeElected
                ()
            {
                AwaitClusterUp(_config.First, _config.Second, _config.Third);

                var oldLeaderAddress = ClusterView.Leader;

                Within(TimeSpan.FromSeconds(30), () =>
                {
                    if (ClusterView.IsLeader)
                    {
                        EnterBarrier("registered-listener");

                        Cluster.Leave(oldLeaderAddress);
                        EnterBarrier("leader-left");

                        // verify that the LEADER is shut down
                        AwaitCondition(() => Cluster.IsTerminated);
                        EnterBarrier("leader-shutdown");
                    }
                    else
                    {
                        var exitingLatch = new TestLatch();

                        var listener = Sys.ActorOf(Props.Create(() => new Listener(oldLeaderAddress, exitingLatch)).WithDeploy(Deploy.Local));

                        Cluster.Subscribe(listener, new []{typeof(ClusterEvent.IMemberEvent)});

                        EnterBarrier("registered-listener");

                        EnterBarrier("leader-left");

                        // verify that the LEADER is EXITING
                        exitingLatch.Ready(TestLatch.DefaultTimeout);

                        EnterBarrier("leader-shutdown");
                        MarkNodeAsUnavailable(oldLeaderAddress);

                        // verify that the LEADER is no longer part of the 'members' set
                        AwaitAssert(() => ClusterView.Members.Select(m => m.Address).Contains(oldLeaderAddress).ShouldBeFalse());

                        // verify that the LEADER is not part of the 'unreachable' set
                        AwaitAssert(() => ClusterView.UnreachableMembers.Select(m => m.Address).Contains(oldLeaderAddress).ShouldBeFalse());

                        // verify that we have a new LEADER
                        AwaitAssert(() => ClusterView.Leader.ShouldNotBe(oldLeaderAddress));
                    }

                    EnterBarrier("finished");
                });
            }
        }

        class Listener : UntypedActor
        {
            readonly Address _oldLeaderAddress;
            readonly TestLatch _latch;

            public Listener(Address oldLeaderAddress, TestLatch latch)
            {
                _oldLeaderAddress = oldLeaderAddress;
                _latch = latch;
            }

            protected override void OnReceive(object message)
            {
                var state = message as ClusterEvent.CurrentClusterState;
                if (state != null)
                {
                    if (state.Members.Any(m => m.Address == _oldLeaderAddress && m.Status == MemberStatus.Exiting))
                        _latch.CountDown();
                }
                var memberExited = message as ClusterEvent.MemberExited;
                if(memberExited != null && memberExited.Member.Address == _oldLeaderAddress)
                    _latch.CountDown();
            }
        }
    }

}
