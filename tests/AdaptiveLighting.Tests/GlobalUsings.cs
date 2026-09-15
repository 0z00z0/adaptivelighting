// Fakes live in AdaptiveLighting.TestFakes, a separate project so tools/uihost can use them without pulling in
// MSTest. This keeps every test file's reference to them unqualified after the move.
global using AdaptiveLighting.TestFakes;
