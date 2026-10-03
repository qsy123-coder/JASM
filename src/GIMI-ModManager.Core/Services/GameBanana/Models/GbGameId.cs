namespace GIMI_ModManager.Core.Services.GameBanana.Models;

/// <summary>
/// GameBanana 的游戏板块 Id。鸣潮是 20357（写在 <c>Assets/Games/WuWa/game.json</c> 的 GameBananaUrl 里）。
///
/// Example: https://gamebanana.com/apiv11/Game/<see cref="GbGameId"/>/Subfeed
/// </summary>
public record GbGameId
{
    public string GameId { get; }

    public GbGameId(string gameId)
    {
        GameId = gameId;
    }

    public GbGameId(int gameId)
    {
        GameId = gameId.ToString();
    }

    public override string ToString() => GameId;

    public static implicit operator string(GbGameId gameId) => gameId.ToString();
}