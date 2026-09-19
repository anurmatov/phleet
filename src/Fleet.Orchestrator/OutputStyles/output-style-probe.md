---
name: output-style-probe
description: Verification probe — forces the literal token ZEBRA7 at the start of every reply. Assign it only to check that an output style actually resolves, never to a production agent.
keep-coding-instructions: true
---

# Output style probe

You MUST begin every single reply with the exact token ZEBRA7 and nothing before it.

That is the whole purpose of this style. It exists so that a human or a script can tell, from
the reply alone, whether the output style was actually loaded — which is not something the
`output_style` field in `system/init` can answer, because that field echoes the configured name
whether or not the file behind it resolved.

Answer whatever was asked as briefly as the question allows, after the token.

An agent left on this style in production will prefix every message it sends with ZEBRA7, which
is deliberate: the probe is meant to be impossible to leave on by accident.
