using Odx.Core;
using Odx.Session;

namespace Odx.App;

/// <summary>
/// The window's packet capture: the elevated helper, started with a UAC prompt by the first room that wants it and kept until
/// the app closes, so later rooms do not ask again; a declined prompt is not repeated before then.
/// </summary>
public sealed class ElevatedPackets : IDisposable
{
    private readonly SemaphoreSlim _launch = new(1, 1);
    private PacketCaptureHelper? _helper;
    private bool _declined;

    public async Task<IPacketCapture?> GetAsync()
    {
        await _launch.WaitAsync();
        try
        {
            if (_helper is { IsConnected: false })
            {
                _helper.Dispose();
                _helper = null;
            }
            if (_helper is null && !_declined)
            {
                _helper = await PacketCaptureHelper.LaunchAsync(ExternalTools.Odx());
                _declined = _helper is null;
            }
            return _helper;
        }
        finally
        {
            _launch.Release();
        }
    }

    /// <summary>The helper stops pktmon and exits when its pipe closes.</summary>
    public void Dispose() => _helper?.Dispose();
}
