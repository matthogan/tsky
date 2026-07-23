# Task Output Close Button UI Regression Test Plan

## Scope
Validate Task Output close-button behavior for:
- X clicked while task is running
- X clicked when run is idle/completed
- X clicked when cancellation request fails

## Preconditions
- Build is green (`dotnet build`).
- At least one task exists that runs long enough to click X while running.
- Task Output pane is visible.
- Use a test group/task where logs are easy to identify.

## Scenario 1: Click X While Running
### Goal
Confirm X follows the same cancellation path as `Esc` on a running task.

### Steps
1. Start a long-running task from a task card.
2. In Task Output, locate that run item while its status indicates running.
3. Click X on that run item.

### Expected
- A cancellation message is appended to that run output item (same message family as keyboard `Esc` cancel).
- The run item is removed from Task Output after successful cancellation request.
- No unhandled exception, no UI freeze.

## Scenario 2: Click X When Idle/Completed
### Goal
Confirm idle/completed runs close immediately without cancellation attempt.

### Steps
1. Run a short task until it completes.
2. In Task Output, click X on that completed run item.

### Expected
- Run item is removed immediately.
- No new cancellation log line is added.
- Remaining output items stay intact.

## Scenario 3: Cancellation Failure Path
### Goal
Confirm failed cancellation prevents removal and writes a failure log line.

### Setup Hint
Use a condition where cancellation cannot be requested (for example, force the run/task mapping to be invalid during debug, or reproduce a state where no active cancellation handle exists but the run is still treated as active by the close path).

### Steps
1. Start a run in the condition above.
2. Click X on that run item.

### Expected
- Run item remains visible (is not removed).
- A log line is appended to that same run output indicating process kill/cancel failure.
- No crash, no UI deadlock.

## Pass Criteria
- All 3 scenarios match expected behavior exactly.
- Behavior is repeatable across at least 2 consecutive attempts.

## Optional Evidence to Capture
- Screenshot before X click and after result for each scenario.
- Copied output lines showing cancellation success/failure messages.
