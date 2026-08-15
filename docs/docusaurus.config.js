const config = {
  title: 'GUSTO',
  tagline: 'A small .NET worker with storage you own',
  url: 'https://bytebardorg.github.io',
  baseUrl: process.env.NODE_ENV === 'development' ? '/' : '/GUSTO/',
  organizationName: 'ByteBardOrg',
  projectName: 'GUSTO',
  onBrokenLinks: 'throw',
  onBrokenMarkdownLinks: 'warn',
  staticDirectories: ['assets'],
  favicon: undefined,

  presets: [
    [
      'classic',
      {
        docs: {
          path: 'content',
          routeBasePath: 'docs',
          sidebarPath: require.resolve('./sidebars.js'),
          editUrl: 'https://github.com/ByteBardOrg/GUSTO/edit/main/docs/content/',
          showLastUpdateAuthor: true,
          showLastUpdateTime: true,
        },
        blog: false,
        theme: {
          customCss: require.resolve('./src/css/custom.css'),
        },
      },
    ],
  ],

  themeConfig: {
    image: 'social-card.png',
    metadata: [
      {name: 'description', content: 'Documentation for GUSTO, a small background job worker for .NET with an application-owned storage contract.'},
    ],
    navbar: {
      title: 'GUSTO',
      items: [
        {type: 'docSidebar', sidebarId: 'story', position: 'left', label: 'Read the guide'},
        {href: 'https://www.nuget.org/packages/ByteBard.GUSTO', label: 'NuGet', position: 'right'},
        {href: 'https://github.com/ByteBardOrg/GUSTO', label: 'GitHub', position: 'right'},
      ],
    },
    footer: {
      style: 'light',
      links: [
        {
          title: 'Project',
          items: [
            {label: 'Source', href: 'https://github.com/ByteBardOrg/GUSTO'},
            {label: 'Issues', href: 'https://github.com/ByteBardOrg/GUSTO/issues'},
            {label: 'License', href: 'https://github.com/ByteBardOrg/GUSTO/blob/main/LICENSE'},
          ],
        },
      ],
      copyright: `GUSTO is maintained by ByteBard and released under the MIT license.`,
    },
    prism: {
      additionalLanguages: ['csharp', 'bash'],
    },
    colorMode: {
      defaultMode: 'light',
      disableSwitch: false,
      respectPrefersColorScheme: true,
    },
  },
};

module.exports = config;
