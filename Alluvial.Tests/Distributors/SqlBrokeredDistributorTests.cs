using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Alluvial.Distributors;
using Alluvial.Distributors.Sql;
using NUnit.Framework;

namespace Alluvial.Tests.Distributors
{
    [TestFixture]
    public class SqlBrokeredDistributorTests : DistributorTests
    {
        private SqlBrokeredDistributor<int> distributor;

        private static readonly SqlBrokeredDistributorDatabase settings = new SqlBrokeredDistributorDatabase
        {
            ConnectionString = @"Data Source=(localdb)\v11.0; Integrated Security=True; MultipleActiveResultSets=False; Initial Catalog=AlluvialSqlDistributor"
        };

        protected override IDistributor<int> CreateDistributor(
            Func<Lease<int>, Task> onReceive = null,
            Leasable<int>[] leasable = null,
            int maxDegreesOfParallelism = 1,
            string name = null,
            TimeSpan? waitInterval = null,
            string scope = null)
        {
            leasable = leasable ?? DefaultLeasable;

            scope = scope ?? DateTimeOffset.UtcNow.Ticks.ToString();
            distributor = new SqlBrokeredDistributor<int>(
                leasable,
                settings,
                scope,
                maxDegreesOfParallelism,
                waitInterval,
                DefaultLeaseDuration);

            if (onReceive != null)
            {
                distributor.OnReceive(onReceive);
            }

            ProvisionLeasableResources(leasable, scope);

            return distributor;
        }

        private void ProvisionLeasableResources(Leasable<int>[] leasable, string scope)
        {
            using (var connection = new SqlConnection(settings.ConnectionString))
            {
                connection.Open();

                foreach (var resource in leasable)
                {
                    var cmd = connection.CreateCommand();
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM [Alluvial].[Leases] 
               WHERE Scope = @scope AND 
               ResourceName = @resourceName)
    BEGIN
        INSERT INTO [Alluvial].[Leases]
                        ([ResourceName],
                         [Scope],
                         [LastGranted],
                         [LastReleased],
                         [Expires])
                 VALUES 
                        (@resourceName, 
                         @scope,
                         @lastGranted,
                         @lastReleased,
                         @expires)
    END";
                    cmd.Parameters.AddWithValue(@"@resourceName", resource.Name);
                    cmd.Parameters.AddWithValue(@"@scope", scope);
                    cmd.Parameters.AddWithValue(@"@lastGranted", resource.LeaseLastGranted);
                    cmd.Parameters.AddWithValue(@"@lastReleased", resource.LeaseLastReleased);
                    cmd.Parameters.AddWithValue(@"@expires", DateTimeOffset.MinValue);
                    cmd.ExecuteScalar();
                }
            }
        }

        protected override TimeSpan DefaultLeaseDuration
        {
            get
            {
                return TimeSpan.FromSeconds(2);
            }
        }

        protected override TimeSpan ClockDriftTolerance
        {
            get
            {
                return TimeSpan.FromSeconds(3);
            }
        }

        [TestFixtureSetUp]
        public void TestFixtureSetUp()
        {
            settings.CreateDatabase().Wait();
            settings.InitializeSchema().Wait();
        }

        [TearDown]
        public void TearDown()
        {
            if (distributor != null)
            {
                distributor.Stop().Wait();
            }
        }
    }
}