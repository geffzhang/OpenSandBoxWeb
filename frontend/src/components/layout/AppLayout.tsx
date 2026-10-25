import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Avatar, Dropdown, Layout, Menu, Space, Typography, theme } from 'antd'
import type { MenuProps } from 'antd'
import {
  AppstoreOutlined,
  BarsOutlined,
  LogoutOutlined,
  PlusOutlined,
  SettingOutlined,
  ThunderboltOutlined,
  UserOutlined,
} from '@ant-design/icons'
import { logout } from '@/auth/auth'
import { useAuth } from '@/auth/AuthContext'

const { Sider, Header, Content } = Layout

const MENU_ITEMS = [
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

export default function AppLayout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { token } = theme.useToken()
  const { me, can } = useAuth()

  const menuItems = MENU_ITEMS.filter((item) => !item.role || can(item.role)).map(
    ({ key, icon, label }) => ({ key, icon, label }),
  )

  const selectedKey =
    menuItems.find((item) => location.pathname.startsWith(item.key))?.key ??
    menuItems[0]?.key ??
    '/settings'

  const userMenu: MenuProps['items'] = [
    {
      key: 'account',
      disabled: true,
      label: `${me.username}（${me.roles.join('、') || '无角色'}）`,
    },
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
          <Dropdown menu={{ items: userMenu }}>
            <Space style={{ cursor: 'pointer' }}>
              <Avatar size="small" icon={<UserOutlined />} />
              <Typography.Text>{me.username}</Typography.Text>
            </Space>
          </Dropdown>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}
