using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared.Random;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class RandomConcurrencyTest
{
    [Test]
    public void SeededSequenceMatchesSystemRandom()
    {
        var random = new RobustRandom();
        random.SetSeed(12345);
        var expected = new Random(12345);
        for (var i = 0; i < 64; i++)
        {
            Assert.That(random.Next(), Is.EqualTo(expected.Next()));
            Assert.That(random.Next(100), Is.EqualTo(expected.Next(100)));
            Assert.That(random.Next(-50, 50), Is.EqualTo(expected.Next(-50, 50)));
            Assert.That(random.NextLong(), Is.EqualTo(expected.NextInt64()));
            Assert.That(random.NextDouble(), Is.EqualTo(expected.NextDouble()));
            var actualBytes = new byte[16];
            var expectedBytes = new byte[16];
            random.NextBytes(actualBytes);
            expected.NextBytes(expectedBytes);
            Assert.That(actualBytes, Is.EqualTo(expectedBytes));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConcurrentDrawsDoNotCorruptSeededRandomness(bool legacyAccess)
    {
        var random = new RobustRandom();
        random.SetSeed(12345);
#pragma warning disable CS0618 // Verify that callers of the legacy backing-generator API are protected too.
        var backing = random.GetRandom();
#pragma warning restore CS0618
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            for (var i = 0; i < 20000; i++)
            {
                if (legacyAccess)
                    backing.Next();
                else
                    random.Next();
            }
        });
        Assert.That(Enumerable.Range(0, 32).Select(_ => random.Next()).Distinct().Count(), Is.GreaterThan(1));
    }
}
