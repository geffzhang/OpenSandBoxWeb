import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Avatar, Dropdown, Layout, Menu, Space, Tag, Typography, theme } from 'antd'
import type { MenuProps } from 'antd'
import {
  AppstoreOutlined,
  BarsOutlined,
  CloudUploadOutlined,
  DatabaseOutlined,
  LogoutOutlined,
  PlusOutlined,
  SettingOutlined,
  TeamOutlined,
  ThunderboltOutlined,
  UserOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import { logout } from '@/auth/auth'
import { useAuth } from '@/auth/AuthContext'

const { Sider, Header, Content } = Layout

const SANDBOX_ITEMS = [
  { key: '/dashboard', icon: <AppstoreOutlined />, label: '仪表盘', role: 'sandbox:viewer' },
  { key: '/sandboxes', icon: <BarsOutlined />, label: '沙箱列表', role: 'sandbox:viewer' },
  { key: '/create', icon: <PlusOutlined />, label: '创建沙箱', role: 'sandbox:admin' },
  {
    key: '/batch-create',
    icon: <ThunderboltOutlined />,
    label: '批量创建',
    role: 'sandbox:admin',
  },
  { key: '/settings', icon: <SettingOutlined />, label: '设置', role: null },
] as const

const VOLUME_ITEMS = [
  { key: '/volumes', icon: <DatabaseOutlined />, label: '卷列表', role: 'volume:viewer' },
  { key: '/volume-tenants', icon: <TeamOutlined />, label: '租户管理', role: 'volume:viewer' },
  {
    key: '/volume-report',
    icon: <WarningOutlined />,
    label: '违规报告',
    role: 'volume:viewer',
  },
  {
    key: '/volume-import',
    icon: <CloudUploadOutlined />,
    label: '回填导入',
    role: 'volume:admin',
  },
] as const

type MenuEntry = (typeof SANDBOX_ITEMS)[number] | (typeof VOLUME_ITEMS)[number]

const toMenuItem = (item: MenuEntry) => ({
  key: item.key,
  icon: item.icon,
  label: item.label,
})

export default function AppLayout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { token } = theme.useToken()
  const { me, can } = useAuth()

  const visibleSandbox = SANDBOX_ITEMS.filter((item) => !item.role || can(item.role))
  const visibleVolume = VOLUME_ITEMS.filter((item) => can(item.role))

  const selectedKey =
    [...visibleSandbox, ...visibleVolume]
      .filter(
        (item) =>
          location.pathname === item.key ||
          location.pathname.startsWith(`${item.key}/`),
      )
      .sort((left, right) => right.key.length - left.key.length)[0]?.key ?? '/settings'

  const menuItems: MenuProps['items'] = [
    ...visibleSandbox.map(toMenuItem),
    ...(visibleVolume.length > 0
      ? [
          {
            type: 'group' as const,
            label: '卷管理',
            children: visibleVolume.map(toMenuItem),
          },
        ]
      : []),
  ]

  const userMenu: MenuProps['items'] = [
    { key: 'account', disabled: true, label: me.username },
    { type: 'divider' },
    { key: 'logout', icon: <LogoutOutlined />, label: '注销', onClick: logout },
  ]

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider width={220} theme="dark">
        <div
          style={{
            height: 56,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          <Typography.Text strong style={{ color: '#fff', fontSize: 16 }}>
            OpenSandbox Admin
          </Typography.Text>
        </div>
        <Menu
          theme="dark"
          mode="inline"
          selectedKeys={[selectedKey]}
          items={menuItems}
          onClick={({ key }) => navigate(key)}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            background: token.colorBgContainer,
            padding: '0 24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
          }}
        >
          <Typography.Title level={4} style={{ margin: 0 }}>
            OpenSandbox 管理控制台
          </Typography.Title>
          <Space size={8}>
            {me.roles.map((role) => (
              <Tag key={role} color={role.endsWith(':admin') ? 'red' : 'blue'}>
                {role}
              </Tag>
            ))}
            <Dropdown menu={{ items: userMenu }}>
              <Space style={{ cursor: 'pointer' }}>
                <Avatar size="small" icon={<UserOutlined />} />
                <Typography.Text>{me.username}</Typography.Text>
              </Space>
            </Dropdown>
          </Space>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}
