DO $bootstrap$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_runtime') THEN
    CREATE ROLE ai_stocks_runtime NOLOGIN NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_worker_runtime') THEN
    CREATE ROLE ai_stocks_worker_runtime NOLOGIN NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_operations_runtime') THEN
    CREATE ROLE ai_stocks_operations_runtime NOLOGIN NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_web_runtime') THEN
    CREATE ROLE ai_stocks_web_runtime NOLOGIN NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_worker') THEN
    CREATE ROLE ai_stocks_worker NOLOGIN NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_operations') THEN
    CREATE ROLE ai_stocks_operations NOLOGIN NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_web') THEN
    CREATE ROLE ai_stocks_web NOLOGIN NOINHERIT;
  END IF;

  GRANT ai_stocks_worker_runtime TO ai_stocks_worker;
  GRANT ai_stocks_operations_runtime TO ai_stocks_operations;
  GRANT ai_stocks_web_runtime TO ai_stocks_web;
END
$bootstrap$;
