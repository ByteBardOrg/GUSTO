import clsx from 'clsx';
import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import CodeBlock from '@theme/CodeBlock';
import styles from './index.module.css';

export default function Home() {
  return (
    <Layout
      title="A background worker with storage you own"
      description="GUSTO is a small .NET background job worker built around an application-owned storage contract."
    >
      <main>
        <header className={styles.hero}>
          <div className={styles.heroInner}>
            <p className={styles.eyebrow}>Background jobs for .NET 8 and .NET 9</p>
            <Heading as="h1">A job runner with extension points.</Heading>
            <p className={styles.lede}>
              GUSTO provides the background worker. Your application defines the
              storage record, persistence, retries, and any additional queue behavior.
            </p>
            <div className={styles.actions}>
              <Link className="button button--primary button--lg" to="/docs/introduction">
                Read the guide
              </Link>
              <Link className="button button--secondary button--lg" to="/docs/build/first-job">
                Run the example
              </Link>
            </div>
          </div>
        </header>

        <section className={styles.example} aria-labelledby="enqueue-example">
          <p className={styles.eyebrow}>Enqueue a job</p>
          <Heading as="h2" id="enqueue-example">A normal, strongly typed method call.</Heading>
          <div className={styles.codeExample}>
            <CodeBlock language="csharp">
{`await jobQueue.EnqueueAsync<EmailJobs>(jobs =>
    jobs.SendWelcomeEmailAsync(userId));`}
            </CodeBlock>
          </div>
          <p>
            GUSTO stores the call through your provider and executes it in the background.
          </p>
        </section>

        <section className={styles.choice}>
          <div>
            <p className={styles.eyebrow}>Package features</p>
            <Heading as="h2">What GUSTO provides</Heading>
          </div>
          <div>
            <p>
              The package includes expression-based enqueueing, the hosted worker,
              configurable concurrency and timeouts, OpenTelemetry instrumentation,
              and test synchronization hooks. Storage and queue policies are supplied
              through your implementation of the public interfaces.
            </p>
            <Link to="/docs/reference/public-api">View the public API →</Link>
          </div>
        </section>
      </main>
    </Layout>
  );
}
