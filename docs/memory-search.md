# Memory search

How fleet-memory finds a memory: what a search returns and in what order, how long memories are
indexed, the settings, and what the first start after an upgrade costs. The same search answers the
`memory_search` tool that agents call and the dashboard's Memory page.

## What a search returns

Each memory is returned once, under the strongest way it matched, in this order:

| Match | When | Needs |
|---|---|---|
| `id` | The whole query is 6 or more hexadecimal digits, with or without the dashes of a UUID, and a memory's id begins with them. Case does not matter. | the files |
| `title` | The query is part of the title, whatever the case. A title that is the query comes first, then one that begins with it. | the files |
| `tag` | The query is one of the memory's tags, whatever the case. | the files |
| `keyword` | A word of the query is in the title, a tag or the content. | the index |
| `meaning` | The memory is near the query by embedding, and no word matched. Carries its similarity as `score`. | the index and the embedder |

`keyword` and `meaning` hits come from one fused ranking (reciprocal rank fusion, the constant 2,
over the best section of each memory on either side), so they can alternate; callers show `meaning`
hits apart from the rest.

Things worth knowing:

- **A query that names a memory by id returns that memory** (and any memory whose title contains
  the same string, or that has it as a tag) **and nothing else.** The index is not asked: the nearest neighbours of a string
  of hexadecimal digits are noise. An ambiguous prefix returns every memory that begins with it. A
  hexadecimal-looking word that begins no memory's id is searched as a word.
- **There is no cut-off by default.** The meaning side always returns its nearest memories, however
  far they are; that is why they are labelled and carry a score. `Search__MinMeaningScore` drops
  the ones below a similarity (see [Settings](#settings)).
- **`id`, `title` and `tag` hits need neither Qdrant nor the embedder.** They come from a catalogue
  held in memory, built from the files before the service starts to listen. With the embedder down, keyword hits still work;
  with Qdrant down, the first three still work. A search that found nothing while a part of it
  could not run fails instead of saying "nothing found".
- **Keywords are words in any script**: runs of Unicode letters, digits and underscore, lower-cased.
  Han and Hiragana, which are written without spaces, are matched one character at a time. Scripts
  that need a dictionary to find word ends (Thai, Lao, Khmer) are matched only as whole runs between
  spaces. English stop words and one-letter words are not keywords.

## Filters

`project`, `type`, `agent`, `tag` (the exact tag) and `since` (memories updated on or after an ISO
8601 date, `2026-03-01`, or date and time, `2026-03-01T09:30:00Z`; no offset means UTC). They apply
to every kind of match.

## Snippets

- `id`, `title`, `tag`: the start of the content.
- `keyword`: about 200 characters around the first place a word of the query occurs, in the section
  that matched.
- `meaning`: the start of the section that matched.

A snippet starts and ends between words, shows runs of white space as one space, and has `…` where
content was left out. `highlights` are `[start, length]` pairs into the snippet, in UTF-16 units,
for the words of the query in it.

## Long memories

A memory whose content is longer than `Search__SectionChars` (1,200 characters) is indexed in
sections: cut before Markdown headings and at paragraph breaks, neighbouring paragraphs packed
together while they fit, a longer paragraph cut at a line or sentence end. Each section is embedded
as the title, a blank line and the section, so a fact deep inside a long memory is found by meaning
as well as one in a short memory. A shorter memory is one section, embedded as before.

A search still returns memories, never sections: one hit per memory, from its best section. Lists,
counts and the duplicate hint of `memory_store` see one entry per memory too.

At most `Search__MaxSections` (200) sections of one memory are indexed; a longer one logs one
warning with its id and the counts, and its remainder is not searchable by keyword or meaning.

## Settings

Plain configuration keys of fleet-memory, read once at start. None has a variable in the shipped
compose file; to change one, add a line such as `- Search__MinMeaningScore=0.35` to the
`fleet-memory` service's `environment:` and recreate the container. An empty value is the default;
a value that cannot be used is the default with one warning in the log.

| Key (env form) | Default | Range | Meaning |
|---|---|---|---|
| `Search__SectionChars` | 1200 | 200 to 8000 | The longest section, in characters. The default fits the smallest supported model (512 tokens) in any script. |
| `Search__MaxSections` | 200 | 1 to 2000 | The most sections of one memory that are indexed. |
| `Search__MinMeaningScore` | 0 (off) | 0 to 1 | A hit found by meaning alone is dropped below this similarity. The right value depends on the embedding model: look at the scores your model gives to results you would and would not want. |

Changing `Search__SectionChars`, `Search__MaxSections`, the embedding provider, its model or the
dimensions re-indexes every memory at the next start. `Search__MinMeaningScore` does not.

## What a start costs

At start fleet-memory reads every memory file, then reads once what the index holds. A file the
index already holds exactly (same content, same metadata, same model and settings) is not embedded
again. One line says what was done:

```
Full index complete: 1904 indexed (3 embedded in 7 sections, 1901 unchanged and not embedded), 0 orphans removed
```

**The first start after upgrading to this version embeds every memory again, once**, because the
keywords and the sections changed: one embedding call per section, one after the other. How long
that takes is the number of sections times what your embedder takes for one input, and both are
yours to measure:

- Sections: a memory whose content is up to 1,200 characters is one. A longer one is roughly one
  per 900 to 1,000 characters (sections are cut at headings and paragraphs, so they are not filled
  to the budget), and never more than 200. A store of many long memories has several times as
  many sections as memories.
- Time per call: the version before this one embedded every memory at every start, so the time
  between its `Starting full index` and `Full index complete` log lines (`docker logs -t
  fleet-memory`), divided by the number it indexed, is what one call cost there. A section is no
  longer than the memories that was measured on, so a call now costs that or less.

The `Full index complete` line of the first start says how many sections there were. During it:

- the service answers; searches by id, title and tag are complete from the first request, because
  the service reads the memory files before it starts to listen;
- a memory not reached yet is still found by keyword and meaning through its old index entry, with
  the old limits (English keywords only, no `since`), until its turn comes;
- a memory written or updated through the API or a tool is indexed at once, as always;
- a file changed on disk by something else is picked up when the scan ends, as before;
- an embedder that fails on a file does not stop the scan: that file keeps its old entry and is
  retried after the scan, with the usual backoff.

An interrupted first start is continued, not repeated: what was embedded is skipped next time.

To go back to an earlier fleet-memory image, drop the index (stop the stack, remove the `qdrant`
container and the `fleet_qdrant_data` volume, start again). An earlier image does not know about
sections and would list and return a long memory several times.

## `GET /internal/memory/search`

Internal, proxied by the orchestrator as `GET /api/memory/search`.

Parameters: `q` (required), `project`, `type`, `tag`, `since`, `limit` (1 to 50, default 20). An
empty optional parameter is no filter. A missing `q`, or a `since` or `limit` that cannot be read,
is a 400 (`invalid_since`, `invalid_limit`).

```json
[
  {
    "id": "5e1f9a3c-7b2d-4c6e-8f10-a1b2c3d4e5f6",
    "title": "Queue notes",
    "project": "alpha",
    "type": "decision",
    "tags": ["queues"],
    "snippet": "…attempts are spaced out. Use backoff between attempts.",
    "highlights": [[30, 7]],
    "match": "keyword",
    "score": null,
    "updated_at": "2026-03-01T12:30:00+00:00"
  }
]
```

`match` is `id`, `title`, `tag`, `keyword` or `meaning`. `score` is a number for `meaning` and
`null` for every other match.

## `memory_search`

Parameters: `query`, `limit` (default 5), `type`, `project`, `agent`, `tag`, `since`. Each result
has a `Match` line saying how it matched (with the similarity for a `meaning` hit) and shows the
snippet. `memory_get` reads a result in full; it, `memory_update` and `memory_delete` take a full id
or the 8-character short id as before.

With project-scoped access on ([docs/acl-rollout.md](acl-rollout.md)), every kind of hit is filtered
by the caller's projects, including a hit by id.
