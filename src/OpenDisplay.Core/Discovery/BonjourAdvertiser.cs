using Makaretu.Dns;

namespace OpenDisplay.Core.Discovery;

/// <summary>
/// Announces this receiver as <c>_opensidecar._tcp</c> with TXT <c>id</c> and <c>pv</c>
/// (PROTOCOL.md 2.1), so the Mac app lists it in its Connection menu.
/// </summary>
public sealed class BonjourAdvertiser : IDisposable
{
    public const string ServiceType = "_opensidecar._tcp";

    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private readonly ServiceProfile _profile;

    public BonjourAdvertiser(string name, string id, int port, int protocolVersion = 3)
    {
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);
        _profile = new ServiceProfile(Sanitize(name), ServiceType, (ushort)port);
        _profile.AddProperty("id", id);
        _profile.AddProperty("pv", protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sd.Advertise(_profile);
        _mdns.Start();
        _sd.Announce(_profile, 3);
    }

    /// <summary>DNS-SD instance names may hold almost anything, but dots would split the label.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = name.Replace('.', ' ').Trim();
        return cleaned.Length == 0 ? "Windows" : cleaned.Length > 63 ? cleaned[..63] : cleaned;
    }

    public void Dispose()
    {
        try
        {
            _sd.Unadvertise(_profile);
        }
        catch (Exception)
        {
            // Best effort goodbye; the record expires on its own.
        }
        _sd.Dispose();
        _mdns.Dispose();
    }
}
