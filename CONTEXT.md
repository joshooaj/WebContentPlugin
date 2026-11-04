# Web Content plugin (Milestone XProtect)

A Smart Client plugin that shows web content in XProtect workspaces, optionally routed through a VMS-side proxy so the Smart Client machine never connects to the web target directly.

## Language

**Proxied boundary**:
The (Windows user, XProtect identity, VMS server) tuple inside which proxied browser state and proxy fetching are shared, and across which they are never shared.
_Avoid_: profile scope, user scope (both under-specify the server leg)

**Listener ownership**:
The state in which exactly one client process serves the loopback fetching for one proxied profile; other processes on the same boundary join it rather than routing their own.
_Avoid_: master/slave, winner takes all

**Joined session**:
A second client process on the same proxied boundary reusing the owner's browser profile and fetching channel. Only ever permitted within one boundary.
_Avoid_: piggyback (kept out of code and docs as a term; it reads as a bug)

**Profile sweep**:
The bounded startup cleanup of one Windows user's plugin data root: reaps orphaned proxied browser-profile folders past the retention window, stale listener-claim files, and claim-create temp litter - shape-bounded, journalled, and never forcing a folder a live browser holds.
_Avoid_: garbage collection, cleanup job (both suggest it runs continuously or may touch anything)
