# MOTD Parsing — BDD Specification (EARS)

**Component:** `MotdParser` (`FCAT/Services/MotdParser.cs`)

## Overview

Extracts clickable in-game chat-channel links from a fleet MOTD. EVE writes channel links as
`<url=joinChannel:...>Label</url>`. The full markup is preserved verbatim so re-emitting it
reproduces a working link (the channel id is inside the markup and cannot be looked up by name).

## Requirements

### Link extraction

**When** `ParseChannelLinks` is called with MOTD text containing `<url=joinChannel:...>Label</url>`
tags, the system shall return a `CapturedChannel` for each tag with the label text and full markup.

### HTML-encoded input

**When** the MOTD is HTML-encoded (ESI often returns `&lt;url=...&gt;`), the system shall decode it
before parsing so the regex sees real tags.

### De-duplication

**When** multiple links share the same label (case-insensitive), the system shall keep only the last
occurrence.

### Empty labels

**When** a link tag has an empty or whitespace-only label, the system shall skip it.

### Empty input

**When** `ParseChannelLinks` is called with null or empty input, the system shall return an empty list.

### Case insensitivity

The system shall match `<url=joinChannel:...>` tags regardless of casing (e.g. `<URL=joinChannel:...>`).

## Test Coverage

- `MotdParserTests.ParseChannelLinks_ExtractsLinks`
- `MotdParserTests.ParseChannelLinks_MultipleLinks`
- `MotdParserTests.ParseChannelLinks_HtmlEncoded_DecodesFirst`
- `MotdParserTests.ParseChannelLinks_DuplicateLabel_LastWins`
- `MotdParserTests.ParseChannelLinks_EmptyLabel_Skipped`
- `MotdParserTests.ParseChannelLinks_Null_ReturnsEmpty`
- `MotdParserTests.ParseChannelLinks_Empty_ReturnsEmpty`
- `MotdParserTests.ParseChannelLinks_CaseInsensitive`
