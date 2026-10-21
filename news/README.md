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
Articles appear once they are on `master`.
