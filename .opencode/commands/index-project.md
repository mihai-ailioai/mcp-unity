---
description: Index Unity project assets for semantic search via Context Engine
---

Call the `index_project` MCP tool with no arguments to perform a full re-index of the Unity project.

This indexes all scripts, prefabs, and scenes (based on the Unity editor's Context Engine settings) into the Augment Context Engine for semantic search via `search_project`.

If the tool responds with a "call again to continue" message, keep calling `index_project` (with no arguments) until it reports completion. This happens on large projects where indexing is split across multiple invocations to avoid timeouts.

Report the final summary when done.
