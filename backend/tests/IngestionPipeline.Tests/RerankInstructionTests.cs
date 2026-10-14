// Decision 34: the rerank instruction is pinned, so an edit fails here instead of silently changing what a
// corpus version records.

using IngestionPipeline.ModelApi;

namespace IngestionPipeline.Tests;

public class RerankInstructionTests
{
    [Fact]
    public void The_instruction_hash_is_the_one_r1_records()
    {
        Assert.Equal("f91b5658748e6e7a0f3d4160e8f29c6c3448aac2618cd90a0100a44d20d03fb0", RerankInstruction.Sha256);
    }
}
