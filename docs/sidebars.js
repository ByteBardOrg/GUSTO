module.exports = {
  story: [
    'introduction',
    {
      type: 'category',
      label: 'Guide',
      collapsible: false,
      items: [
        'build/how-it-works',
        'build/first-job',
        'build/enqueueing',
        'build/storage-provider',
        'operate/configuration',
        'operate/failures',
        'operate/observability',
        'operate/testing',
      ],
    },
    {
      type: 'category',
      label: 'Examples',
      collapsible: false,
      items: [
        'extend/overview',
        'extend/ef-core-provider',
        'extend/batches',
        'extend/continuations',
        'extend/batch-continuations',
        'extend/recurring-jobs',
      ],
    },
    {
      type: 'category',
      label: 'Reference',
      collapsible: true,
      collapsed: true,
      items: [
        'reference/public-api',
        'reference/expressions-and-serialization',
        'reference/worker-runtime',
      ],
    },
  ],
};
