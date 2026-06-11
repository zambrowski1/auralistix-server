# Auralistix Community on Timeweb Cloud

This repository has a Timeweb-friendly Docker setup for the `Auralistix.Server` ASP.NET Core community API.

## What is included

- `Dockerfile` builds and runs only `Auralistix.Server`.
- `.dockerignore` keeps local desktop app builds, logs, and local dev data out of the image.
- `.env.timeweb.example` lists the Timeweb variables to add in the app panel.
- Production mode is configured for Timeweb PostgreSQL + Timeweb S3-compatible object storage.

## Timeweb deployment

1. Create a new app from the repository.
2. Choose Dockerfile deployment and point it at the repository root.
3. Set the app port to `8080` if Timeweb asks for it. The Dockerfile exposes `8080`, and the server also respects `PORT`, `URLS`, and `ASPNETCORE_URLS`.
4. Attach your domain, for example `api.auralistix.pro`, and enable HTTPS.
5. Add the variables from `.env.timeweb.example` in the Timeweb app variables UI.
6. Replace all `YOUR_...` placeholders with values from Timeweb PostgreSQL and S3.
7. Replace `Jwt__SigningKey` with a private random secret. Production startup fails if the development key is still used.
8. Configure SMTP with `noreply@auralistix.pro`; `support@auralistix.pro` is used as the reply-to address.
9. Open `/api/health` on the deployed domain. A healthy response returns `status: ok`.

After deployment, open the desktop app, go to Community settings, and set the API base URL to the same `Community__PublicBaseUrl`.

## Timeweb variables checklist

- `Database__Provider=Postgres`
- `ConnectionStrings__Community=Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true`
- `Community__StorageProvider=S3`
- `S3__ServiceUrl=https://s3.twcstorage.ru`
- `S3__Region=ru-1`
- `S3__BucketName=...`
- `S3__AccessKey=...`
- `S3__SecretKey=...`
- `Smtp__Host=smtp.timeweb.ru`
- `Smtp__Username=noreply@auralistix.pro`
- `Smtp__FromAddress=noreply@auralistix.pro`
- `Smtp__ReplyToAddress=support@auralistix.pro`
