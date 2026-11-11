# minitui.org/desk

The guides and API reference for Desk, built with [docfx](https://dotnet.github.io/docfx/):

```bash
dotnet tool restore
dotnet docfx docs/site/docfx.json --serve     # http://localhost:8080
```

CI builds them with `--warningsAsErrors`, so a broken link or reference fails the pull request.

The site itself is built by the [minitui repo](https://github.com/minitui-org/minitui)'s `site.yml`, from this repo's
latest release tag, into `minitui.org/desk`. It adds minitui's own template, for the site's styles and to leave the spec
references out of the API pages, so a local build looks plainer than the site. After a release here, run that workflow
by hand.
