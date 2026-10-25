#!/usr/bin/env bash
# Uploads news videos to the site's media folder: upload-media.sh <file>... → https://meitou.kenshi.wiki/media/<file name>
# The files live in the meitou-site container's /usr/share/nginx/html/media volume (Cloudovna), not in git or the image; a file of
# the same name is replaced. Needs SSH as root to the container's node, which is the site's own host, over IPv4. One connection per
# file: the node throttles quick reconnects (docs/launcher.md, "News").
set -eu
[ $# -gt 0 ] || { echo "usage: upload-media.sh <file>..." >&2; exit 1; }
host=meitou.kenshi.wiki
container=svc-01a14751-a46c-7b3a-b469-6b5eac0ba3e8
for file in "$@"; do
  name=$(basename "$file")
  # Streamed into a temporary name first, so a half-uploaded file is never served.
  ssh -4 "root@$host" "set -e
    dir=\$(docker volume inspect -f '{{.Mountpoint}}' \$(docker inspect -f '{{range .Mounts}}{{if eq .Destination \"/usr/share/nginx/html/media\"}}{{.Name}}{{end}}{{end}}' $container))
    cat > \"\$dir/.$name.part\" && chmod 644 \"\$dir/.$name.part\" && mv -f \"\$dir/.$name.part\" \"\$dir/$name\"" < "$file"
  echo "https://$host/media/$name"
done
