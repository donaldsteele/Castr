using Castr.Core.Transport;

namespace Castr.Gui.Services;

/// <summary>
/// Creates the multicast transports the Send/Receive flows drive their sessions over. Abstracted so the
/// desktop head can plug in real UDP multicast while headless tests substitute an in-process fake LAN and
/// still exercise the exact same view-model + session code path end-to-end.
/// </summary>
public interface ITransportFactory
{
    IMulticastTransport CreateSenderTransport();

    IMulticastTransport CreateReceiverTransport();
}
