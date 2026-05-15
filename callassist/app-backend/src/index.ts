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
import { callbackRouter } from "./routes/callbackRouter";
import { healthRouter } from "./routes/healthRouter";
import { audioRouter } from "./routes/audioRouter";
import { signalrRouter } from "./routes/signalrRouter";
import { attachMediaStreamServer } from "./audio/mediaStreamServer";

const app = express();

app.use(helmet());
app.use(cors());
app.use(express.json());

app.use((req, _res, next) => {
  console.log(`--> ${req.method} ${req.path}`);
  next();
});

app.use("/calls", callbackRouter);
app.use("/health", healthRouter);
app.use("/audio", audioRouter);
app.use("/", signalrRouter);

const server = app.listen(config.app.port, () => {
  console.log(`app-backend listening on port ${config.app.port} (${config.app.nodeEnv})`);
});

attachMediaStreamServer(server);

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
