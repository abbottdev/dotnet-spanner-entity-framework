// Copyright 2021 Google LLC
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     https://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Cloud.EntityFrameworkCore.Spanner.IntegrationTests.Model;
using Google.Cloud.Spanner.Data;
using System;
using System.Collections.Generic;
using Xunit;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Threading.Tasks;
using Google.Cloud.EntityFrameworkCore.Spanner.Storage;

namespace Google.Cloud.EntityFrameworkCore.Spanner.IntegrationTests
{
    public class TransactionTests : IClassFixture<SpannerSampleFixture>
    {
        private readonly SpannerSampleFixture _fixture;

        public TransactionTests(SpannerSampleFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task SaveChangesIsAtomic()
        {
            var singerId = _fixture.RandomLong();
            var invalidSingerId = _fixture.RandomLong();
            var albumId = _fixture.RandomLong();
            using (var db = new TestSpannerSampleDbContext(_fixture.DatabaseName))
            {
                // Try to add a singer and an album in one transaction.
                // The album is invalid. Both the singer and the album
                // should not be inserted.
                db.Singers.Add(new Singers
                {
                    SingerId = singerId,
                    FirstName = "Joe",
                    LastName = "Elliot",
                });
                db.Albums.Add(new Albums
                {
                    AlbumId = albumId,
                    SingerId = invalidSingerId, // Invalid, does not reference an actual Singer
                    Title = "Some title",
                });
                await Assert.ThrowsAsync<SpannerBatchNonQueryException>(() => db.SaveChangesAsync());
            }

            using (var db = new TestSpannerSampleDbContext(_fixture.DatabaseName))
            {
                // Verify that the singer was not inserted in the database.
                Assert.Null(await db.Singers.FindAsync(singerId));
            }
        }

        [Fact]
        public async Task EndOfTransactionScopeCausesRollback()
        {
            var venueCode = _fixture.RandomString(4);
            using var db = new TestSpannerSampleDbContext(_fixture.DatabaseName);
            using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.Venues.AddRange(new Venues
                {
                    Code = venueCode,
                    Name = "Venue 3",
                });
                await db.SaveChangesAsync();
                // End the transaction scope without any explicit rollback.
            }
            // Verify that the venue was not inserted.
            var venuesAfterRollback = db.Venues
                .Where(v => v.Code == venueCode)
                .ToList();
            Assert.Empty(venuesAfterRollback);
        }

        [Fact]
        public async Task TransactionCanReadYourWrites()
        {
            var venueCode1 = _fixture.RandomString(4);
            var venueCode2 = _fixture.RandomString(4);
            using var db = new TestSpannerSampleDbContext(_fixture.DatabaseName);

            using var transaction = await db.Database.BeginTransactionAsync();
            // Add two venues in the transaction.
            db.Venues.AddRange(new Venues
            {
                Code = venueCode1,
                Name = "Venue 1",
            }, new Venues
            {
                Code = venueCode2,
                Name = "Venue 2",
            });
            await db.SaveChangesAsync();

            // Verify that we can read the venue while inside the transaction.
            var venues = db.Venues
                .Where(v => v.Code == venueCode1 || v.Code == venueCode2)
                .OrderBy(v => v.Name)
                .ToList();
            Assert.Equal(2, venues.Count);
            Assert.Equal("Venue 1", venues[0].Name);
            Assert.Equal("Venue 2", venues[1].Name);
            // Rollback and then verify that we should not be able to see the venues.
            await transaction.RollbackAsync();

            // Verify that the venues can no longer be read.
            var venuesAfterRollback = db.Venues
                .Where(v => v.Code == venueCode1 || v.Name == venueCode2)
                .ToList();
            Assert.Empty(venuesAfterRollback);
        }

        [Fact]
        public async Task CanUseSharedContextAndTransaction()
        {
            var venueCode = _fixture.RandomString(4);
            using var connection = _fixture.GetConnection();
            var options = new DbContextOptionsBuilder<SpannerSampleDbContext>()
                .UseSpanner(connection)
                .Options;
            using var context1 = new TestSpannerSampleDbContext(options);
            using var transaction = context1.Database.BeginTransaction();
            using (var context2 = new TestSpannerSampleDbContext(options))
            {
                await context2.Database.UseTransactionAsync(DbContextTransactionExtensions.GetDbTransaction(transaction));
                context2.Venues.Add(new Venues
                {
                    Code = venueCode,
                    Name = "Venue 3",
                });
                await context2.SaveChangesAsync();
            }
            // Check that the venue is readable from the other context.
            Assert.Equal("Venue 3", (await context1.Venues.FindAsync(venueCode)).Name);
            await transaction.CommitAsync();
            // Verify that it is also readable from a new unrelated context.
            using var context3 = new TestSpannerSampleDbContext(_fixture.DatabaseName);
            Assert.Equal("Venue 3", (await context1.Venues.FindAsync(venueCode)).Name);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TransactionRetry(bool enableInternalRetries)
        {
            const int transactions = 8;
            var aborted = new List<Exception>();
            var res = Parallel.For(0, transactions, (i, state) =>
            {
                try
                {
                    // The internal retry mechanism should be able to catch and retry
                    // all aborted transactions. If internal retries are disabled, multiple
                    // transactions will abort.
                    InsertRandomSinger(enableInternalRetries).Wait();
                }
                catch (AggregateException e) when (e.InnerException is SpannerException se && se.ErrorCode == ErrorCode.Aborted)
                {
                    lock (aborted)
                    {
                        aborted.Add(se);
                    }
                    // We don't care exactly how many transactions were aborted, only whether
                    // at least one or none was aborted.
                    state.Stop();
                }
            });
            // If a transaction is aborted, the parallel operations will not run to completion.
            Assert.Equal(enableInternalRetries, res.IsCompleted);
            Assert.Equal(enableInternalRetries, aborted.Count == 0);
        }

        private async Task InsertRandomSinger(bool enableInternalRetries)
        {
            var rnd = new Random();
            using var context = new TestSpannerSampleDbContext(_fixture.DatabaseName);
            using var transaction = await context.Database.BeginTransactionAsync();
            var retriableTransaction = (SpannerRetriableTransaction)transaction.GetDbTransaction();
            retriableTransaction.EnableInternalRetries = enableInternalRetries;

            var rows = rnd.Next(1, 10);
            for (var row = 0; row < rows; row++)
            {
                // This test assumes that this is random enough and that the id's
                // will never overlap during a test run.
                var id = _fixture.RandomLong();
                var prefix = id.ToString("D20");
                // First name is required, so we just assign a meaningless random value.
                var firstName = "FirstName" + "-" + rnd.Next(10000).ToString("D4");
                // Last name contains the same value as the primary key with a random suffix.
                // This makes it possible to search for a singer using the last name and knowing
                // that the search will at most deliver one row (and it will be the same row each time).
                var lastName = prefix + "-" + rnd.Next(10000).ToString("D4");

                // Yes, this is highly inefficient, but that is intentional. This
                // will cause a large number of the transactions to be aborted.
                var existing = await context
                    .Singers
                    .Where(v => EF.Functions.Like(v.LastName, prefix + "%"))
                    .OrderBy(v => v.LastName)
                    .FirstOrDefaultAsync();

                if (existing == null)
                {
                    context.Singers.Add(new Singers
                    {
                        SingerId = id,
                        FirstName = firstName,
                        LastName = lastName,
                    });
                }
                else
                {
                    existing.FirstName = firstName;
                }
                await context.SaveChangesAsync();
            }
            await transaction.CommitAsync();
        }
    }
}
