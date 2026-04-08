---
name: fleet-review
description: Code review checklist and PR review workflow for Fleet QA agents
---

# Fleet Code Review

## Review Checklist

### Correctness
- [ ] Code does what the PR description says
- [ ] Edge cases are handled
- [ ] Error handling is appropriate
- [ ] No regressions in existing functionality

### Security
- [ ] No hardcoded secrets or credentials
- [ ] Input validation on external data
- [ ] No SQL injection, XSS, or command injection risks
- [ ] Authentication/authorization checks in place

### Quality
- [ ] Code follows project conventions (CLAUDE.md)
- [ ] No unnecessary complexity
- [ ] Tests cover the changes
- [ ] All tests pass

### Performance
- [ ] No obvious N+1 queries
- [ ] No unbounded collections or loops
- [ ] Async operations used where appropriate

## Review Output Format
```markdown
## Review: {PR Title}

**Verdict**: Approve / Request Changes

### Summary
Brief overview of the changes and their quality.

### Issues Found
1. **[severity]** file:line — description

### Suggestions
- Optional improvements (not blocking)
```
