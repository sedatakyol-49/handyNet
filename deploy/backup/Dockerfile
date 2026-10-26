# Backup sidecar: pg_dump + optional S3-compatible upload on a daily schedule. No secrets are baked in; all via env.
FROM postgres:16-alpine
RUN apk add --no-cache aws-cli tar
COPY backup-service.sh /usr/local/bin/backup-service.sh
RUN chmod +x /usr/local/bin/backup-service.sh
USER root
ENTRYPOINT ["/usr/local/bin/backup-service.sh"]
CMD ["daemon"]
