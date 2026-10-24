# Example rice

Copy it to your own Studio config and edit it. The Studio picks the files up live.

    mkdir -p ~/.config/ukiyo && cp -R studio/examples/rice ~/.config/ukiyo/studio

Same shape as `studio/`: `studio.json`, `themes/`, `layouts/`, `panels/`, plus `user.css` (loaded last). A file here
replaces the repo file of the same name; `studio.json` fields override the repo's. `$UKIYO_STUDIO_CONFIG` or
`$XDG_CONFIG_HOME` move the directory. Contents: a `midnight` theme (`"extends": "graphite"`, only changed tokens), a
`rice` layout, a `clock` panel with an option, and a small `user.css`.
