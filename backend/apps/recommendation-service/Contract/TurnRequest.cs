// One question and the language the answer is asked for ("auto" detects it from the question).

namespace RecommendationService.Contract;

public sealed record TurnRequest(string Question, string Language);
