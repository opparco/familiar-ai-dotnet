An embodied AI agent has an action plan and just executed one step.
Decide whether the observation BLOCKS further progress on the plan.

"Blocked" means: the observation contradicts a key assumption in the plan,
or makes the next planned step impossible/pointless.
"NOT blocked" means: the step succeeded or partially succeeded and the plan can continue.

Plan:
{plan}

Step executed: {tool}({args_summary})
Observation received: {result_summary}

Reply with exactly one word: "blocked" or "ok".
