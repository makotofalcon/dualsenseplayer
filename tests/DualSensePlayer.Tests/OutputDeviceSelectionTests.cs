using DualSensePlayer.Core;
using Xunit;

namespace DualSensePlayer.Tests;

public sealed class OutputDeviceSelectionTests
{
    private static AudioDevice Endpoint(string id, string container = "", HidDevice? hid = null, int channels = 4, int rate = 48000)
        => new(id, "Wireless Controller", container, channels, rate, hid);

    [Fact]
    public void ExplicitFourChannelAudioEndpointWorksWithoutHidOrContainerId()
    {
        var endpoint = Endpoint("selected");
        Assert.True(endpoint.CanSendHaptics);
        Assert.Same(endpoint, OutputDeviceSelection.Match(new() { EndpointId = "selected" }, [endpoint], true));
        Assert.Null(OutputDeviceSelection.Match(new(), [endpoint], true));
        Assert.Null(OutputDeviceSelection.Match(new() { EndpointId = "missing" }, [endpoint], true));
    }

    [Fact]
    public void TemporaryHidFailureDoesNotPreventAudioToRegisteredEndpoint()
    {
        var endpoint = Endpoint("selected", "controller-one");
        var registered = new OutputSettings { EndpointId = "selected", ContainerId = "controller-one", Serial = "serial-one" };
        Assert.Same(endpoint, OutputDeviceSelection.Match(registered, [endpoint], true));
        var unreadableSerial = endpoint with { Hid = new("hid", "controller-one", "") };
        Assert.Same(unreadableSerial, OutputDeviceSelection.Match(registered, [unreadableSerial], true));
        Assert.Null(OutputDeviceSelection.Match(registered, [Endpoint("other", "other-controller")], true));
        var wrongDevice = endpoint with { Hid = new("hid", "controller-one", "different-serial") };
        Assert.Null(OutputDeviceSelection.Match(registered, [wrongDevice], true));
    }

    [Fact]
    public void ReconnectedControllerKeepsItsSlotAndAmbiguousCandidatesAreRejected()
    {
        var endpoint = Endpoint("new-id", "controller-one", new("hid", "controller-one", "serial-one"));
        var registered = new OutputSettings { EndpointId = "old-id", ContainerId = "controller-one", Serial = "serial-one" };
        Assert.Same(endpoint, OutputDeviceSelection.Match(registered, [endpoint, Endpoint("third", "third-controller")], true));
        Assert.Null(OutputDeviceSelection.Match(registered, [endpoint, endpoint with { Id = "ambiguous" }], true));
        Assert.Null(OutputDeviceSelection.Match(registered, [endpoint with { ContainerId = "other" }], true));
    }

    [Theory]
    [InlineData(2, 48000)]
    [InlineData(6, 48000)]
    [InlineData(4, 44100)]
    public void UnidentifiedIncompatibleEndpointsCannotReceiveHaptics(int channels, int rate)
    {
        var endpoint = Endpoint("selected", channels: channels, rate: rate);
        Assert.False(endpoint.CanSendHaptics);
        Assert.Null(OutputDeviceSelection.Match(new() { EndpointId = "selected" }, [endpoint], true));
    }

    [Fact]
    public void KnownDualSenseRemainsSelectableWhenSharedFormatNeedsCorrection()
    {
        var endpoint = Endpoint("selected", "one", new("hid", "one", "serial"), channels: 2);
        Assert.True(endpoint.CanSendHaptics);
        Assert.Same(endpoint, OutputDeviceSelection.Match(new() { EndpointId = "selected", ContainerId = "one" }, [endpoint], true));
        Assert.Null(OutputDeviceSelection.Match(new() { EndpointId = "selected" }, [endpoint], false));
    }
}
