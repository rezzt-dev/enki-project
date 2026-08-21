using Xunit;

// LocalizationService.Instance is a process-wide static singleton, and several test classes
// (LocalizationServiceTests, LanguageSetupViewModelTests, RelativeTimeFormatterTests, PluralTests) call
// SetLanguage on it to exercise culture-dependent formatting, restoring the original language when they're
// done. xUnit runs different test classes in parallel by default, so without this a class that briefly
// switches the language races every other class reading a localized string on another thread — e.g.
// PolarAuthServiceTests asserting a specific localized error message could observe "fr-FR" mid-switch and
// fail intermittently, even though nothing about Polar/Supabase itself is broken. Serializing the whole
// assembly removes the race at its source instead of chasing it class by class; the suite is fast enough
// (well under a second) that losing parallelism here is not a meaningful cost.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
