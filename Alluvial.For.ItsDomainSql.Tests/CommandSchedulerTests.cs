using System;
using System.Collections.Generic;
using System.Data.Entity;
using FluentAssertions;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Alluvial.Distributors.Sql;
using Alluvial.Tests;
using Microsoft.Its.Domain;
using Microsoft.Its.Domain.Sql;
using Microsoft.Its.Domain.Sql.CommandScheduler;
using NUnit.Framework;
using Clock = Microsoft.Its.Domain.Clock;
using static Alluvial.For.ItsDomainSql.Tests.TestDatabases;

namespace Alluvial.For.ItsDomainSql.Tests
{
    [TestFixture]
    public class CommandSchedulerTests
    {
        private CompositeDisposable disposables;
        private string clockName;
        private IStreamQueryRangePartition<Guid>[] partitionsByAggregateId;

        [SetUp]
        public void SetUp()
        {
            clockName = Guid.NewGuid().ToString();
            disposables = new CompositeDisposable();

            var configuration = new Configuration()
                .UseSqlEventStore(
                    c => c.UseConnectionString(EventStoreConnectionString))
                .UseSqlStorageForScheduledCommands(
                    c => c.UseConnectionString(CommandSchedulerConnectionString))
                .UseDependency<GetClockName>(c => _ => clockName)
                .TraceScheduledCommands();

            partitionsByAggregateId = Partition.AllGuids().Among(16).ToArray();

            disposables.Add(ConfigurationContext.Establish(configuration));
        }

        [TearDown]
        public void TearDown()
        {
            disposables.Dispose();
        }

        [Test]
        public async Task Commands_on_a_single_clock_can_be_processed_as_a_stream()
        {
            // arrange
            await ScheduleSomeCommands(50);

            var commandsDue = CommandScheduler.CommandsDueOnClock(clockName);

            var distributor = partitionsByAggregateId.CreateSqlBrokeredDistributor(
                new SqlBrokeredDistributorDatabase(CommandSchedulerConnectionString),
                commandsDue.Id,
                waitInterval: TimeSpan.FromSeconds(.5));

            var catchup = commandsDue
                .CreateDistributedCatchup(distributor);

            var store = new InMemoryProjectionStore<CommandsApplied>();

            catchup.Subscribe(CommandScheduler.DeliverScheduledCommands().Trace(), store);

            // act
            await catchup.RunSingleBatch().Timeout();

            // assert
            store.Sum(c => c.Value.Count).Should().Be(50);

            using (var db = Configuration.Current.CommandSchedulerDbContext())
            {
                var remainingCommandsDue = await db.ScheduledCommands
                                          .Where(c => c.Clock.Name == clockName)
                                          .Due()
                                          .CountAsync();
                remainingCommandsDue.Should().Be(0);
            }
        }

        [Test]
        public async Task When_the_distributor_runs_then_the_clock_can_be_kept_updated()
        {
            // arrange
            var commandsDue = CommandScheduler.CommandsDueOnClock(clockName);

            DateTimeOffset timePriorToDeliveringCommands;
            using (var db = Configuration.Current.CommandSchedulerDbContext())
            {
                timePriorToDeliveringCommands = db.Clocks
                    .Single(c => c.Name == "default")
                    .UtcNow;
            }

            var distributor = partitionsByAggregateId
                .CreateSqlBrokeredDistributor(
                    new SqlBrokeredDistributorDatabase(CommandSchedulerConnectionString),
                    commandsDue.Id,
                    waitInterval: TimeSpan.FromSeconds(.5))
                .KeepClockUpdated();

            var catchup = commandsDue
                .CreateDistributedCatchup(distributor);

            catchup.Subscribe(CommandScheduler.DeliverScheduledCommands().Trace());

            // act
            await catchup.RunSingleBatch().Timeout();

            // assert
            using (var db = Configuration.Current.CommandSchedulerDbContext())
            {
                db.Clocks
                    .Single(c => c.Name == "default")
                    .UtcNow
                    .Should()
                    .BeAfter(timePriorToDeliveringCommands);
            }
        }

        [Test]
        public async Task repro_of_some_commands_not_being_delivered_sucesfully()
        {
            // arrange
            await ScheduleSomeCommands(5);

            var commandsDue = CommandScheduler.CommandsDueOnClock(clockName);

            var distributor = partitionsByAggregateId.CreateSqlBrokeredDistributor(
                new SqlBrokeredDistributorDatabase(CommandSchedulerConnectionString),
                commandsDue.Id,
                waitInterval: TimeSpan.FromSeconds(.5));

            var catchup = commandsDue
                .CreateDistributedCatchup(distributor);

            var store = new InMemoryProjectionStore<CommandsApplied>();

            catchup.Subscribe(CommandScheduler.DeliverScheduledCommands().Trace(), store);

            // act
            await catchup.RunSingleBatch().Timeout();

            // assert
            store.Sum(c => c.Value.Count).Should().Be(5);

            using (var db = Configuration.Current.CommandSchedulerDbContext())
            {
                var remainingCommandsDue = await db.ScheduledCommands
                                                   .Where(c => c.Clock.Name == clockName)
                                                   .Due()
                                                   .CountAsync();
                remainingCommandsDue.Should().Be(0);
            }
        }

        private static async Task<IEnumerable<IScheduledCommand<NotAnAggreateTarget>>> ScheduleSomeCommands(
            int howMany = 20,
            DateTimeOffset? dueTime = null,
            Func<string> clockName = null)
        {
            if (clockName != null)
            {
                Configuration.Current.UseDependency<GetClockName>(c => _ => clockName());
            }

            var commandsScheduled = new List<IScheduledCommand<NotAnAggreateTarget>>();

            foreach (var id in Enumerable.Range(1, howMany).Select(_ => Guid.NewGuid()))
            {
                var scheduler = Configuration.Current.CommandScheduler<NotAnAggreateTarget>();
                var command = await scheduler.Schedule(id,
                                                       new DoSomething()
                                                       {
                                                       },
                                                       dueTime);
                commandsScheduled.Add(command);
            }

            return commandsScheduled;
        }
    }
}