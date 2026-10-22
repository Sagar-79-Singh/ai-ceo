
### 📄 4. `docs/UI_SPEC.md` (Frontend Spec)

```markdown
# UI Specification (Blazor Server or Razor Pages)

## Layout
- Two-column layout (Sidebar 25%, Main Chat 75%).
- Dark mode by default.

## Sidebar (Agent Management)
- List of agents (Name, Role, IsManager).
- "Create Agent" button opening a modal.
- Modal fields: Name, Role, System Prompt (textarea), Model ID (default `gh/gpt-5.4-nano`).

## Main Feed (Execution)
- Chat-like interface.
- User enters a prompt.
- Show CEO's plan clearly.
- Show Worker delegations (e.g., "[Researcher] is thinking...").
- Show final CEO response.
