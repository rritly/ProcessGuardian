using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Services.Logging;
using ProcessGuardian.Tests.Mocks;
using ProcessGuardian.Core.Logging;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System;

namespace ProcessGuardian.Tests.Logging
{
    [TestClass]
    public class RingLoggerTests
    {
        [TestMethod]
        public async Task AddSingleEntry_PresentInBuffer()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "Test single entry");
            await logger.FlushAsync();

            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsFalse(string.IsNullOrEmpty(content));
            Assert.IsTrue(content.Contains("Test single entry"));
        }

        [TestMethod]
        public async Task BufferFull_TriggersAutomaticFlush()
        {
            var storage = new MockFileStorage();
            // set very long periodic interval so only signal triggers flush
            var logger = new RingLogger(storage, capacity: 3, flushInterval: TimeSpan.FromSeconds(60));

            logger.Log(LogLevel.Information, "F1");
            logger.Log(LogLevel.Information, "F2");
            logger.Log(LogLevel.Information, "F3"); // should trigger automatic flush

            // wait up to 2 seconds for automatic flush to complete
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? content = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                content = await storage.ReadAllTextAsync("log.txt");
                if (!string.IsNullOrEmpty(content)) break;
                await Task.Delay(50);
            }

            Assert.IsFalse(string.IsNullOrEmpty(content));
            Assert.IsTrue(content.Contains("F1") && content.Contains("F2") && content.Contains("F3"));
        }

        [TestMethod]
        public async Task OrderIsPreserved()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "First");
            logger.Log(LogLevel.Information, "Second");
            logger.Log(LogLevel.Information, "Third");
            await logger.FlushAsync();

            var content = await storage.ReadAllTextAsync("log.txt");
            var idx1 = content.IndexOf("First");
            var idx2 = content.IndexOf("Second");
            var idx3 = content.IndexOf("Third");
            Assert.IsTrue(idx1 >= 0 && idx2 > idx1 && idx3 > idx2);
        }

        [TestMethod]
        public async Task RingBufferOverflow_EvictsOldest()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 3, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "A");
            logger.Log(LogLevel.Information, "B");
            logger.Log(LogLevel.Information, "C");
            logger.Log(LogLevel.Information, "D"); // should evict A
            await logger.FlushAsync();

            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsFalse(content.Contains("A"));
            Assert.IsTrue(content.Contains("B") && content.Contains("C") && content.Contains("D"));
        }

        [TestMethod]
        public async Task ConcurrentWriters_NoCorruption()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 100, flushInterval: TimeSpan.FromMilliseconds(50));
            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() => logger.Log(LogLevel.Information, $"M{i}"))).ToArray();
            await Task.WhenAll(tasks);
            await logger.FlushAsync();

            var content = await storage.ReadAllTextAsync("log.txt");
            for (int i = 0; i < 50; i++)
            {
                Assert.IsTrue(content.Contains($"M{i}"));
            }
        }

        [TestMethod]
        public async Task DisabledLogging_NoWrites()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50), enabled: false);
            logger.Log(LogLevel.Information, "ShouldNotWrite");
            await logger.FlushAsync();

            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(string.IsNullOrEmpty(content));
        }

        [TestMethod]
        public async Task Flush_WritesToStorage()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Warning, "Warn me");
            await logger.FlushAsync();

            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("[Warning]"));
            Assert.IsTrue(content.Contains("Warn me"));
        }

        [TestMethod]
        public async Task StorageWriteFailure_NoCrash()
        {
            var storage = new MockFileStorage { ThrowOnWrite = true };
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Error, "Will fail");
            await logger.FlushAsync(); // should not throw

            // buffer should still contain the entry because write failed
            // now allow write and flush again
            storage.ThrowOnWrite = false;
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("Will fail"));
        }

        [TestMethod]
        public async Task StorageWriteFailure_RetainsBufferedEntries()
        {
            var storage = new MockFileStorage { ThrowOnWrite = true };
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Error, "PersistMe");
            await logger.FlushAsync();
            storage.ThrowOnWrite = false;
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("PersistMe"));
        }

        [TestMethod]
        public async Task Shutdown_FlushesBuffer()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "OnShutdown");
            await logger.ShutdownAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("OnShutdown"));
        }

        [TestMethod]
        public async Task CancellationDuringFlush()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "CancelMe");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            // call flush with already-cancelled token; should complete without throwing and not corrupt
            await logger.FlushAsync(cts.Token);
            // subsequent flush should still work
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("CancelMe"));
        }

        [TestMethod]
        public async Task NoParallelFlushes()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "P1");
            logger.Log(LogLevel.Information, "P2");

            var f1 = logger.FlushAsync();
            var f2 = logger.FlushAsync();
            await Task.WhenAll(f1, f2);

            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("P1") && content.Contains("P2"));
        }

        [TestMethod]
        public async Task TimestampFormat_IsUtcIso8601()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Information, "TimeCheck");
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            var firstLine = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).First();
            // parse timestamp part
            var parts = firstLine.Split(' ');
            Assert.IsTrue(DateTimeOffset.TryParse(parts[0], out var dto));
            Assert.AreEqual("Z", parts[0].Last().ToString());
        }

        [TestMethod]
        public async Task LogLevelRecordedCorrectly()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Warning, "Lvl");
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("[Warning]"));
        }

        [TestMethod]
        public async Task LineFormatConsistency()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            logger.Log(LogLevel.Error, "LineFormat");
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            var line = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).First();
            Assert.IsTrue(line.Contains("[Error]") && line.Contains("LineFormat"));
        }

        [TestMethod]
        public async Task DiskLog_IsBoundedToLogBufferSize()
        {
            var storage = new MockFileStorage();
            var capacity = 5;
            var logger = new RingLogger(storage, capacity: capacity, flushInterval: TimeSpan.FromMilliseconds(50));
            for (int i = 0; i < 10; i++) logger.Log(LogLevel.Information, $"X{i}");
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            // count entries by header detection
            var entries = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("Z [Information]")).ToArray();
            Assert.AreEqual(capacity, entries.Length);
        }

        [TestMethod]
        public async Task NewEntriesDuringFlush_AreNotLost()
        {
            var storage = new MockFileStorage { ThrowOnWrite = false };
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(200));
            logger.Log(LogLevel.Information, "Before");
            // start flush in background
            var flushTask = Task.Run(async () => await logger.FlushAsync());
            // add new entry while flush may be in progress
            logger.Log(LogLevel.Information, "During");
            await flushTask;
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsTrue(content.Contains("Before") && content.Contains("During"));
        }

        [TestMethod]
        public async Task Shutdown_PreventsNewWrites()
        {
            var storage = new MockFileStorage();
            var logger = new RingLogger(storage, capacity: 10, flushInterval: TimeSpan.FromMilliseconds(50));
            await logger.ShutdownAsync();
            logger.Log(LogLevel.Information, "AfterShutdown");
            await logger.FlushAsync();
            var content = await storage.ReadAllTextAsync("log.txt");
            Assert.IsFalse(content?.Contains("AfterShutdown") ?? false);
        }
    }
}
