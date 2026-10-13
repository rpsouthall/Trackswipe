using System.Runtime.InteropServices;
using TrackSwipe.Core;
using TrackSwipe.Touchpad;

namespace TrackSwipe.Tests;

public class InputStorageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void SnapshotOwnsEveryContactEvenAfterReaderReusesBuffer(int count)
    {
        var contacts = Enumerable.Range(0, count).Select(i => new Contact(i, i, -i, true, i != 2)).ToArray();
        var snapshot = TouchFrame.FromContacts(1, contacts.AsSpan(), 110, 70);
        Array.Fill(contacts, default);
        Assert.Equal(count, snapshot.Contacts.Count);
        for (var i = 0; i < count; i++) Assert.Equal(new Contact(i, i, -i, true, i != 2), snapshot.Contacts[i]);
        Assert.Equal(count, snapshot.Contacts.ToArray().Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Contacts[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Contacts[count]);
    }

    [Fact]
    public void ValueEqualityComparesContactsBeyondTheFirstInlineSlot()
    {
        var contacts = Enumerable.Range(0, 5).Select(i => new Contact(i, i, 0, true, true)).ToArray();
        var first = TouchFrame.FromContacts(1, contacts.AsSpan(), 110, 70);
        var equal = TouchFrame.FromContacts(1, contacts.AsSpan(), 110, 70);
        Assert.Equal(first, equal);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        contacts[4] = contacts[4] with { X = 42 };
        Assert.NotEqual(first, TouchFrame.FromContacts(1, contacts.AsSpan(), 110, 70));
    }

    [Fact]
    public void ActiveTrackingAndDisplayResamplingDoNotAllocateAfterWarmup()
    {
        var queue = new TouchFrameQueue();
        var engine = new GestureEngine(new GestureOptions { Fingers = 4 });
        void Report(int i)
        {
            Span<Contact> contacts = stackalloc Contact[4];
            var time = i / 125.0;
            for (var finger = 0; finger < 4; finger++)
                contacts[finger] = new Contact(finger, 30 + finger * 12 + Math.Sin(time) * 10, 40, true, true);
            queue.Enqueue(TouchFrame.FromContacts(time, contacts, 110, 70));
            while (queue.TryDequeue(out var frame)) engine.Process(frame);
            engine.Tick(time + 1 / 240.0);
        }
        for (var i = 0; i < 10000; i++) Report(i);
        Assert.Equal(GesturePhase.Tracking, engine.Phase);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 10000; i < 20000; i++) Report(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void QueueWrapsAndGrowsWithoutDroppingLiftReports()
    {
        var queue = new TouchFrameQueue();
        for (var i = 0; i < 50; i++) queue.Enqueue(Frame(i));
        for (var i = 0; i < 40; i++) AssertNext(queue, i);
        for (var i = 50; i < 300; i++) queue.Enqueue(Frame(i));
        for (var i = 40; i < 300; i++) AssertNext(queue, i);
        Assert.True(queue.IsEmpty);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task ConcurrentProducersAndConsumerPreserveEachProducersOrder()
    {
        var queue = new TouchFrameQueue();
        const int count = 10000;
        var producers = Enumerable.Range(0, 2).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < count; i++) queue.Enqueue(Frame(producer * count + i));
        })).ToArray();
        var next = new int[2];
        var consumer = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            for (var received = 0; received < count * 2;)
            {
                if (!queue.TryDequeue(out var frame))
                {
                    Assert.True(DateTime.UtcNow < deadline, "Queue failed to deliver all reports.");
                    Thread.Yield();
                    continue;
                }
                var value = (int)frame.Time;
                var producer = value / count;
                Assert.Equal(next[producer]++, value % count);
                received++;
            }
        });
        await Task.WhenAll(producers.Append(consumer));
        Assert.Equal(new[] { count, count }, next);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void NormalInputPipelineDoesNotAllocateAfterWarmup()
    {
        var contacts = new List<Contact>(5);
        for (var i = 0; i < 4; i++) contacts.Add(new Contact(i, 30 + i * 12, 40, true, true));
        var queue = new TouchFrameQueue();
        var engine = new GestureEngine(new GestureOptions { Fingers = 4 });
        void Report(int i)
        {
            queue.Enqueue(TouchFrame.FromContacts(i / 125.0, CollectionsMarshal.AsSpan(contacts), 110, 70));
            while (queue.TryDequeue(out var frame)) engine.Process(frame);
        }
        for (var i = 0; i < 10000; i++) Report(i);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 10000; i < 20000; i++) Report(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static TouchFrame Frame(int value) => TouchFrame.FromContacts(value,
        new[] { new Contact(value, value, 0, value % 7 != 0, true) }.AsSpan(), 110, 70);

    private static void AssertNext(TouchFrameQueue queue, int value)
    {
        Assert.True(queue.TryDequeue(out var frame));
        Assert.Equal(value, frame.Time);
        Assert.Equal(value, frame.Contacts[0].Id);
        Assert.Equal(value % 7 != 0, frame.Contacts[0].Tip);
    }
}
