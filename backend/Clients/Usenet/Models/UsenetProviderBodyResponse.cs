using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Models;

public sealed record UsenetProviderBodyResponse(UsenetDecodedBodyResponse Response, string ProviderHost);
