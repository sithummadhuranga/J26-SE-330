// The follow-up chat's use cases: start a session from a stored answer, read it, and answer a question. Model calls
// run outside any transaction (they take seconds); the turn is then stored in one short transaction and read back,
// so what is sent is what was stored. Depends only on interfaces.

using System.Text;
using System.Text.Json.Nodes;
using RecommendationService.Configuration;
using RecommendationService.Contract;
using WoundRag.Core.Chat;
using WoundRag.Infrastructure.Chat;
using WoundRag.Infrastructure.Corpus;

namespace RecommendationService.Chat;

public sealed class ChatUseCase(IChatRepository chats, ICorpusRepository corpus, ChatTurnEngine engine, ChatRequestReader reader,
    RecommendationSettings settings, TimeProvider clock)
{
    public async Task<ChatResult> StartAsync(string body, CancellationToken ct)
    {
        var (request, problems) = reader.ReadSession(body);
        if (request is null) return new(422, ErrorBody.ValidationFailed(problems));
        if (await corpus.LoadFrozenAsync(settings.CorpusVersion, ct) is null) return CorpusUnavailable();

        var start = await chats.StartAsync(request.CaseId, request.Revision, settings.CorpusVersion, clock.GetUtcNow(), ct);
        switch (start.Kind)
        {
            case ChatStartKind.NoAnswer: return new(404, ErrorBody.NoAnswer());
            case ChatStartKind.CorpusDiffers: return new(409, ErrorBody.CorpusDiffers());
        }
        var session = await chats.LoadAsync(start.SessionId!.Value, ct)
            ?? throw new InvalidOperationException($"session {start.SessionId} was started but cannot be loaded");
        return new(start.Kind == ChatStartKind.Started ? 201 : 200, Bytes(ChatJson.Session(session)));
    }

    public async Task<ChatResult> GetAsync(Guid sessionId, CancellationToken ct) =>
        await chats.LoadAsync(sessionId, ct) is { } session ? new(200, Bytes(ChatJson.Session(session))) : new(404, ErrorBody.NotFound());

    public async Task<ChatResult> AskAsync(Guid sessionId, string body, CancellationToken ct)
    {
        var session = await chats.LoadAsync(sessionId, ct);
        if (session is null) return new(404, ErrorBody.NotFound());
        var (request, problems) = reader.ReadTurn(body);
        if (request is null) return new(422, ErrorBody.ValidationFailed(problems));
        var frozen = await corpus.LoadFrozenAsync(session.CorpusVersion, ct);
        if (frozen is null || session.CorpusVersion != settings.CorpusVersion) return CorpusUnavailable();

        var input = new ChatTurnInput(request.Question, request.Language, session.Findings, session.Withheld, session.History());
        var turn = await engine.RunAsync(input, session.Pack(), new SessionGuidelineSearch(chats, frozen.Info, session.Observed), ct);

        var number = session.Turns.Count + 1;
        if (!await chats.SaveTurnAsync(session, number, turn, clock.GetUtcNow(), ct)) return new(409, ErrorBody.TurnConflict());
        var stored = await chats.LoadAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"session {sessionId} disappeared after turn {number} was stored");
        return new(200, Bytes(ChatJson.Turn(stored, stored.Turns.Single(t => t.Number == number))), turn.Alert);
    }

    private ChatResult CorpusUnavailable() =>
        new(503, ErrorBody.Unavailable($"Corpus {settings.CorpusVersion} is not stored or not frozen."));

    private static byte[] Bytes(JsonObject body) => Encoding.UTF8.GetBytes(body.ToJsonString());
}
