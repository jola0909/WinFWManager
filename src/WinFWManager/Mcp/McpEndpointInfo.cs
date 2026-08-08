using System.Security.Cryptography;

namespace WinFWManager.Mcp;

/// <summary>
/// Connection details for the local MCP endpoint. The token is generated per app run
/// and never persisted — stopping the server invalidates it.
/// </summary>
public sealed class McpEndpointInfo
{
    public int Port { get; }
    public string Token { get; }

    public McpEndpointInfo(int port, string token)
    {
        Port = port;
        Token = token;
    }

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>The command a user pastes into a terminal to register this server.</summary>
    public string ClaudeCliCommand =>
        $"claude mcp add --transport http winfw {Url} --header \"Authorization: Bearer {Token}\"";

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
