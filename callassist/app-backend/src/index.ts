import * as dotenv from "dotenv";
dotenv.config();
import * as appInsights from "applicationinsights";

if (process.env["APPLICATIONINSIGHTS_CONNECTION_STRING"]) {
  appInsights.setup().start();
}

import express from "express";
import helmet from "helmet";
import cors from "cors";
import { config } from "./config/config";
import { healthRouter } from "./routes/healthRouter";
import { signalrRouter } from "./routes/signalrRouter";
import { attachAudioIngestServer } from "./bot/audioIngestServer";

const app = express();

const allowedOrigins = (process.env["ALLOWED_ORIGINS"] ?? "")
  .split(",")
  .map((s) => s.trim())
  .filter((s) => s.length > 0);

const corsOptions: cors.CorsOptions = allowedOrigins.length > 0
  ? {
      origin: (origin, cb) => {
        if (!origin) return cb(null, true); // same-origin / server-to-server
        if (allowedOrigins.includes(origin)) return cb(null, true);
        return cb(new Error(`CORS: origin not allowed: ${origin}`));
      },
      credentials: true,
    }
  : { origin: true, credentials: true };

app.use(helmet());
app.use(cors(corsOptions));
app.use(express.json());

app.use((req, _res, next) => {
  console.log(`--> ${req.method} ${req.path}`);
  next();
});

app.use("/health", healthRouter);
app.use("/", signalrRouter);

const server = app.listen(config.app.port, () => {
  console.log(`app-backend listening on port ${config.app.port} (${config.app.nodeEnv})`);
});

attachAudioIngestServer(server);

process.on("SIGTERM", () => {
  console.log("SIGTERM received — shutting down");
  server.close((err) => {
    if (err) {
      console.error("Error during server close", err);
      process.exit(1);
    }
    console.log("HTTP server closed");
    process.exit(0);
  });
});
