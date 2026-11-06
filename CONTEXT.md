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

**Handoff**:
The transition in which a boundary's listener owner disappears (clean exit or death) and a surviving attached process takes the slot through the claim protocol at a fresh generation (epoch), after which the boundary's open proxied views are re-created so the browser process re-launches with the new port. Rides the claim protocol's own audits and the standard WebView re-creation route - never a claim side-door, never an argument hot-swap on a running browser process.
_Avoid_: failover (suggests hot standby), takeover alone (in the claim protocol it names only the slot write, not the view re-creation the generation implies)

**Profile sweep**:
The bounded startup cleanup of one Windows user's plugin data root: reaps orphaned proxied browser-profile folders past the retention window, stale listener-claim files, and claim-create temp litter - shape-bounded, journalled, and never forcing a folder a live browser holds.
_Avoid_: garbage collection, cleanup job (both suggest it runs continuously or may touch anything)
