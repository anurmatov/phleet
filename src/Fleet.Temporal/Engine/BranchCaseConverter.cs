// BranchCaseConverter was removed in favor of making break/continue proper step types.
// Branch cases are now always StepDefinition; {"type":"break"} and {"type":"continue"}
// are handled via JsonPolymorphic on StepDefinition (BreakStep / ContinueStep).
