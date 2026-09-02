global using Xunit;

// OpenAiClientTests and TestGeneratorTests mutate the process-wide OPENAI_API_KEY /
// OPENAI_MODEL environment variables; disable cross-class parallelization so they
// don't race each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]