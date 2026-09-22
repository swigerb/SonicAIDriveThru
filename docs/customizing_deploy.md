# Customizing the VoiceRAG deployment

This guide shows you how to customize the [VoiceRAG](../README.md#deploying-the-app) deployment to specify different options.
If your goal is to reuse existing services (OpenAI or Search), see the [existing services guide](./existing_services.md) instead.

## Customizing the real-time voice choice

The default carhop voice is `marin` (set in `app/backend/config.yaml` `model.default_voice` and in
`infra/main.parameters.json`). Guests can also switch voices live from the settings dialog.
To change the deployed default, run:

```bash
azd env set AZURE_OPENAI_REALTIME_VOICE_CHOICE <marin, cedar, alloy, ash, ballad, coral, echo, sage, shimmer, or verse>
```

These are the ten built-in voices `gpt-realtime-2.1` accepts (other names such as `fable`, `onyx` or `nova`
are rejected). OpenAI recommends `marin` and `cedar` for the best quality.

> An `azd env` value overrides the default in `infra/main.parameters.json`, so an environment created before
> the default changed keeps its old voice until you `azd env set` it.

Once you have set the voice choice, run `azd up` to apply the changes to the deployed app.
If you've already run `azd up` and want to first preview the voice with the development server, then update your local `.env` file by running `./scripts/write_env.sh` or `pwsh ./scripts/write_env.ps1`, and then restart the development server.
