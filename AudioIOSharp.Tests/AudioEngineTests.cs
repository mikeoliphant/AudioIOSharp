namespace AudioIOSharp.Tests;

public class AudioEngineTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    static async Task Blocks(TestClient client, int count)
    {
        for (int i = 0; i < count; i++)
            Assert.True(await client.BlockRan.WaitAsync(Timeout), $"block {i + 1} of {count} didn't run");
    }

    [Fact]
    public async Task TheClockRunsTheClient()
    {
        using var engine = new AudioEngine(new AudioConfig());
        var client = new TestClient();
        await engine.Attach(client);
        await Blocks(client, 10);

        AudioState state = engine.State;
        Assert.True(state.Open);
        Assert.True(state.Running);
        Assert.Equal(ClockDriver.Name, state.Driver);
        Assert.False(state.Fallback);
        Assert.Equal(1, client.Prepared);
        Assert.Equal(ClockDriver.DefaultSampleRate, client.Format!.SampleRate);

        await engine.Detach(client);
        Assert.False(engine.State.Open);
        Assert.False(engine.State.Running);
    }

    [Fact]
    public async Task TheClockAllocatesNothingBetweenBlocks()
    {
        using var engine = new AudioEngine(new AudioConfig());
        var client = new TestClient();
        await engine.Attach(client);
        await Blocks(client, 60);
        await engine.Detach(client);
        Assert.Equal(0, client.AllocatedBetweenBlocks);
    }

    [Fact]
    public async Task TheAudioPassesThroughTheBuffers()
    {
        var fake = new FakeDriver();
        using var engine = new AudioEngine(new AudioConfig { Driver = "Fake" }, [fake]);
        var client = new TestClient();
        await engine.Attach(client);

        FakeStream stream = fake.Stream!;
        for (int n = 0; n < stream.Inputs[0].Length; n++)
            stream.Inputs[0][n] = n * 0.001f;
        stream.Run();

        Assert.Equal(2, stream.Outputs.Length); // the first two outputs, by default
        Assert.Equal(stream.Inputs[0], stream.Outputs[0]);
        Assert.Equal(stream.Inputs[0], stream.Outputs[1]);
    }

    [Fact]
    public async Task ADeviceThatWontOpenFallsBackToTheClock()
    {
        var fake = new FakeDriver { OpenError = "No device is connected." };
        using var engine = new AudioEngine(new AudioConfig { Driver = "Fake" }, [fake]);
        var logged = new List<string>();
        engine.Log += (_, message) => logged.Add(message);
        var client = new TestClient();
        await engine.Attach(client);
        await Blocks(client, 5);

        AudioState state = engine.State;
        Assert.True(state.Fallback);
        Assert.Equal(ClockDriver.Name, state.Driver);
        Assert.Equal("No device is connected.", state.Error);
        Assert.Equal("Fake", engine.Config.Driver); // still configured: it's used again once it can be opened
        Assert.Contains(logged, m => m.Contains("running on the internal clock") && m.Contains("No device is connected."));
    }

    [Fact]
    public async Task ADeviceFailingWhileRunningSwitchesToNoDeviceAtTheSameRate()
    {
        var fake = new FakeDriver();
        using var engine = new AudioEngine(new AudioConfig { Driver = "Fake", SampleRate = 44100 }, [fake]);
        var reset = new TaskCompletionSource<AudioConfig>();
        engine.ConfigChanged += c => reset.TrySetResult(c);
        var client = new TestClient();
        await engine.Attach(client);
        fake.Stream!.Run();
        await Blocks(client, 1);

        fake.Stream.Fail("The device was unplugged.");
        AudioConfig config = await reset.Task.WaitAsync(Timeout);

        Assert.Equal(ClockDriver.Name, config.Driver);
        Assert.Equal(44100, config.SampleRate);
        Assert.Equal(config, engine.Config);
        Assert.True(fake.Stream.Closed);
        Assert.Equal("The device was unplugged.", engine.State.Error);
        await Blocks(client, 5); // the client carries on, on the clock
        Assert.Equal(ClockDriver.Name, engine.State.Driver);
        Assert.Equal(44100, client.Format!.SampleRate);
    }

    [Fact]
    public async Task AConfigurationTheDeviceDoesntOfferKeepsThePreviousOne()
    {
        var fake = new FakeDriver();
        using var engine = new AudioEngine(new AudioConfig { Driver = "Fake" }, [fake]);
        var client = new TestClient();
        await engine.Attach(client);
        FakeStream stream = fake.Stream!;

        var ex = await Assert.ThrowsAsync<AudioException>(() => engine.SetConfig(engine.Config with { SampleRate = 96000 }));

        Assert.Equal(AudioErrorKind.InvalidValue, ex.Kind);
        Assert.Equal(new[] { 44100, 48000 }, (IReadOnlyList<int>)ex.Details!);
        Assert.Null(engine.Config.SampleRate);
        Assert.Same(stream, fake.Stream); // checked before closing: the running stream wasn't touched
        Assert.False(stream.Closed);
    }

    [Fact]
    public async Task ANewConfigurationMovesTheRunningClient()
    {
        var a = new FakeDriver("A");
        var b = new FakeDriver("B");
        using var engine = new AudioEngine(new AudioConfig { Driver = "A" }, [a, b]);
        var client = new TestClient();
        await engine.Attach(client);

        AudioConfig applied = await engine.SetConfig(new AudioConfig { Driver = "B", SampleRate = 44100, BufferSize = 128 });

        Assert.Equal("B", applied.Driver);
        Assert.Equal(FakeDriver.DeviceName, applied.Device); // filled in
        Assert.True(a.Stream!.Closed);
        Assert.Equal(2, client.Prepared);
        Assert.Equal(44100, client.Format!.SampleRate);
        Assert.Equal(128, client.Format.MaxFrames);
        b.Stream!.Run();
        await Blocks(client, 1);
    }

    [Fact]
    public async Task AResidentStreamOpensAtStartAndSwapsClientsWithoutClosing()
    {
        var jackLike = new FakeDriver("JackLike", resident: true);
        using var engine = new AudioEngine(new AudioConfig { Driver = "JackLike" }, [jackLike]);
        await engine.Start();
        Assert.Equal(1, jackLike.Opened);
        Assert.True(engine.State.Open);
        Assert.False(engine.State.Running);

        var first = new TestClient();
        await engine.Attach(first);
        jackLike.Stream!.Run();
        await Blocks(first, 1);

        var second = new TestClient();
        await engine.Attach(second);
        jackLike.Stream.Run();
        await Blocks(second, 1);
        Assert.Equal(1, first.Blocks);

        await engine.Detach(second);
        Assert.Equal(1, jackLike.Opened);
        Assert.False(jackLike.Stream.Closed);
        Assert.True(engine.State.Open);
        Assert.False(engine.State.Running);
    }

    [Fact]
    public void AnUnknownDriverIsNotFound()
    {
        using var engine = new AudioEngine(new AudioConfig());
        var ex = Assert.Throws<AudioException>(() => engine.Driver("Nope"));
        Assert.Equal(AudioErrorKind.NotFound, ex.Kind);
    }
}
