# News

Posts shown on the Meitou launcher's Home page, newest first (docs/launcher.md, "News"). One folder per article, named
`<yyyy-mm-dd>-<slug>`, holding `index.md` and the article's pictures:

```
news/2026-10-19-meitou-launcher/
  index.md
  cover.png
```

`index.md` is Markdown after an optional front matter block:

```
---
title: Post title
date: 2026-10-19
author: VlcekM
image: cover.png
---
```

`author` shows next to the date; `image` is an optional cover; images in the text (`![alt](screenshot.png)`) and links are relative to the article's folder.
A `.webm` file works wherever an image does (`image: clip.webm`, `![](clip.webm)`): it shows its first frame with a play button and plays with sound on a click,
with pause, 10 s skips and a seek bar. `video: <url>` adds a "Full video" button to a video cover (GitHub plays no repository `.webm` in the browser: its file page only offers the raw download; a video dragged into release notes plays there). Video must be VP8 or VP9 (8-bit), sound Vorbis (Opus is played silent), so encode with
`ffmpeg -i in.mp4 -c:v libvpx-vp9 -crf 34 -b:v 0 -c:a libvorbis -q:a 4 clip.webm`; only its first MB is fetched for the poster, the rest when it is played.
Articles appear once they are on `master`.
