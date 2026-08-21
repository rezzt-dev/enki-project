using Xunit;

namespace enki.Tests.Localization;

/// <summary>Tests that mutate <see cref="enki.Localization.LocalizationService.Instance.CurrentCulture"/>
/// or depend on its value must not run in parallel with each other, because the active culture is a
/// process-wide singleton.</summary>
[CollectionDefinition("LocalizationMutatingTests")]
public class LocalizationMutatingTestsCollection : ICollectionFixture<object>
{
    // Marker class — no shared state needed.
}
