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
A `.webm` works wherever an image does: it shows its first frame with a play button and plays with sound on a click, with pause,
10 s skips and a seek bar; `video: <url>` adds a "Full video" button to a video cover. Videos are not committed: upload them with
`tools/scripts/upload-media.sh clip.webm full.mp4` (the site's media folder, outside git) and use the URLs it prints
(`image: https://meitou.kenshi.wiki/media/clip.webm`). The `.webm` must be VP8 or VP9 (8-bit) with Vorbis sound (Opus plays silent):
`ffmpeg -i in.mp4 -c:v libvpx-vp9 -b:v 3M -c:a libvorbis -q:a 4 clip.webm`; only its first MB is fetched for the poster, the rest on
Play. The full video is an H.264 `.mp4` with `-movflags +faststart`, so browsers play it as it loads.
Articles appear once they are on `master`.
