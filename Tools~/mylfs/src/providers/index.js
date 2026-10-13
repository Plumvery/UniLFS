import { PROVIDER_DRIVE, PROVIDER_LOCAL } from '../project.js';
import { S3Provider } from './s3.js';
import { GoogleDriveProvider } from './gdrive.js';
import { LocalProvider } from './local.js';

export { S3Provider, GoogleDriveProvider, LocalProvider };
export { StorageError, ConfigError } from './http.js';

/**
 * A provider is { displayName, blobExists(hash, signal),
 * uploadBlob(hash, src, onBytes, signal), downloadBlob(hash, dest, onBytes, signal),
 * testConnection(signal) }. Pass your own to the core functions to plug in
 * other storage.
 */
export function createProvider(project) {
  const s = project.settings;
  const c = project.credentials();
  if (s.provider === PROVIDER_DRIVE) {
    return new GoogleDriveProvider({
      clientId: c.driveClientId,
      clientSecret: c.driveClientSecret,
      refreshToken: c.driveRefreshToken,
      folderId: s.driveFolderId,
    });
  }
  if (s.provider === PROVIDER_LOCAL) {
    return new LocalProvider({ localPath: s.localPath, projectRoot: project.root });
  }
  return new S3Provider({
    endpoint: s.s3Endpoint,
    bucket: s.s3Bucket,
    region: s.s3Region,
    prefix: s.s3Prefix,
    accessKeyId: c.s3AccessKeyId,
    secretAccessKey: c.s3SecretAccessKey,
  });
}
