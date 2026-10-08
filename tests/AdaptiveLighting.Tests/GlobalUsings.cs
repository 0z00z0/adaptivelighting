// Fakes live in AdaptiveLighting.TestFakes, a separate project so tools/uihost can use them without pulling in
// MSTest. This keeps every test file's reference to them unqualified after the move.
global using AdaptiveLighting.TestFakes;

// The shared namespaces: configuration (Adaptive.Settings) and web components (Adaptive.UI). Adaptive.Engine comes from the
// project file.
global using Adaptive.Settings;
global using Adaptive.UI;
