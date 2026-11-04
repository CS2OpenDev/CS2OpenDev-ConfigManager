### Windows SmartScreen

`acb-win-x64.exe` is not code-signed yet, so Windows does not know its publisher. Your browser may say the file is not commonly downloaded, and the first run may show "Windows protected your PC". Check the file first (below), then keep the download and choose **More info**, then **Run anyway**. If Smart App Control is on, Windows blocks unsigned programs with no option to run them; the warnings go away once releases are code-signed.

### Checking a download

With the [GitHub CLI](https://cli.github.com/), in the folder holding the files:

```
gh attestation verify acb-win-x64.exe --repo CS2OpenDev/CS2OpenDev-ConfigManager \
  --signer-workflow CS2OpenDev/CS2OpenDev-ConfigManager/.github/workflows/release.yml --source-ref refs/tags/{tag}
sha256sum -c --ignore-missing SHA256SUMS
```

That says the file was built by this repository's release workflow from the `{tag}` tag and has not changed since. Use `acb-linux-x64` in place of `acb-win-x64.exe` for the Linux build.

